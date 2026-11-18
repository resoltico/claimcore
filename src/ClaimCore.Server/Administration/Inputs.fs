namespace ClaimCore.Server.Administration

open ClaimCore.CommandLine

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Globalization
open System.Text
open ClaimCore.Json
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.RecordFormat

type Command =
    | Initialize of timeZone: string * owner: InitialOwner
    | RecoverOwner of OwnerRecoveryInput
    | Verify of InstallationCheckpoint option
    | Rebuild of InstallationCheckpoint * MaintenanceHandle
    | AdmitRestoredInstallation of InstallationCheckpoint * MaintenanceHandle
    | Head
    | Status
    | Maintenance of MaintenanceChange
    | Help
    | Version

[<RequireQualifiedAccess>]
type CommandName =
    | Initialize
    | RecoverOwner
    | Verify
    | Rebuild
    | AdmitRestoredInstallation
    | Head
    | Status
    | Maintenance
    | Help
    | Version

module Command =
    [<Literal>]
    let ProgramName = "claimcore-server"

    let supportedExitCodes =
        [
            ExitCode.Success
            ExitCode.Rejected
            ExitCode.Failed
            ExitCode.OutcomeUnknown
            ExitCode.Diverged
            ExitCode.Usage
        ]

    let name =
        function
        | Initialize _ -> CommandName.Initialize
        | RecoverOwner _ -> CommandName.RecoverOwner
        | Verify _ -> CommandName.Verify
        | Rebuild _ -> CommandName.Rebuild
        | AdmitRestoredInstallation _ -> CommandName.AdmitRestoredInstallation
        | Head -> CommandName.Head
        | Status -> CommandName.Status
        | Maintenance _ -> CommandName.Maintenance
        | Help -> CommandName.Help
        | Version -> CommandName.Version

[<RequireQualifiedAccess>]
type CommandArgument =
    | Operand of string
    | Optional of flag: string * value: string

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CommandName =
    let vocabulary =
        Vocabulary.ofUnion<CommandName> "command" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary

    let token name =
        Vocabulary.token vocabulary name
        |> _.ToLowerInvariant()
        |> fun value -> value.Replace('_', '-')

    let parse value =
        all |> List.tryFind (fun name -> token name = value)

    let arguments =
        function
        | CommandName.Initialize ->
            [
                CommandArgument.Operand "TIME_ZONE"
                CommandArgument.Operand "--owner-issuer URL"
                CommandArgument.Operand "--owner-subject SUBJECT"
                CommandArgument.Operand "--owner-name NAME"
                CommandArgument.Optional("--owner-contact", "TEXT")
                CommandArgument.Optional("--owner-role", "ROLE")
            ]
        | CommandName.RecoverOwner ->
            [
                CommandArgument.Operand "--owner-issuer URL"
                CommandArgument.Operand "--owner-subject SUBJECT"
                CommandArgument.Optional("--owner-name", "NAME")
                CommandArgument.Optional("--actor-id", "ID")
            ]
        | CommandName.Verify -> [ CommandArgument.Optional("--checkpoint", "FILE") ]
        | CommandName.Rebuild ->
            [
                CommandArgument.Operand
                    "--checkpoint FILE --pause-generation GENERATION --pause-id PAUSE_ID"
            ]
        | CommandName.Maintenance ->
            [
                CommandArgument.Operand
                    "acquire GENERATION PAUSE_ID | resume GENERATION PAUSE_ID | takeover GENERATION PAUSE_ID NEW_PAUSE_ID"
            ]
        | CommandName.AdmitRestoredInstallation ->
            [
                CommandArgument.Operand
                    "--checkpoint FILE --pause-generation GENERATION --pause-id PAUSE_ID"
            ]
        | CommandName.Head
        | CommandName.Status
        | CommandName.Help
        | CommandName.Version -> []

    let synopsis name =
        let argument =
            function
            | CommandArgument.Operand value -> value
            | CommandArgument.Optional(flag, value) -> "[" + flag + " " + value + "]"

        String.concat
            " "
            (Command.ProgramName :: token name :: (arguments name |> List.map argument))

[<RequireQualifiedAccess>]
type CommandInputFailure =
    | Usage of UsageFrame
    | Operator of OperatorFault

type private CommandParseFailure =
    | UsageProblem of UsageError
    | OperatorProblem of OperatorFault

module Inputs =
    let private checkpoint path =
        match PrivateFile.read CheckpointRecord.MaximumBytes path with
        | Error _ -> Error(OperatorProblem OperatorFault.CheckpointFileRefused)
        | Ok bytes ->
            try
                CheckpointRecord.decode bytes
                |> Result.mapError (fun _ -> OperatorProblem OperatorFault.CheckpointInvalid)
            finally
                CryptographicOperations.ZeroMemory bytes

    let private noArguments command arguments =
        if List.isEmpty arguments then
            Ok command
        else
            Error(UsageError.NotApplicable("arguments", "command"))

    let private ownerValueFlags =
        [ "--owner-issuer"; "--owner-subject"; "--owner-name"; "--owner-contact" ]

    let private initialize arguments =
        let rec options values roles =
            function
            | [] -> Ok(values, List.rev roles)
            | "--owner-role" :: raw :: rest ->
                match Role.parse raw with
                | Some role when
                    List.contains role ClaimCore.Domain.Tasks.Registry.initialOwnerRoles
                    && not (List.contains role roles)
                    ->
                    options values (role :: roles) rest
                | _ ->
                    Error(
                        UsageError.InvalidChoice(
                            "--owner-role",
                            raw,
                            ClaimCore.Domain.Tasks.Registry.initialOwnerRoles |> List.map Role.token
                        )
                    )
            | flag :: raw :: rest when List.contains flag ownerValueFlags ->
                if raw.StartsWith("--", StringComparison.Ordinal) then
                    Error(UsageError.MissingValue flag)
                elif Map.containsKey flag values then
                    Error(UsageError.DuplicateOption flag)
                else
                    options (Map.add flag raw values) roles rest
            | [ flag ] when flag = "--owner-role" || List.contains flag ownerValueFlags ->
                Error(UsageError.MissingValue flag)
            | flag :: _ -> Error(UsageError.UnknownOption(flag, None))

        match arguments with
        | [] -> Error(UsageError.MissingOperand "TIME_ZONE")
        | zone :: flags ->
            options Map.empty [] flags
            |> Result.bind (fun (values, roles) ->
                match
                    [ "--owner-issuer"; "--owner-subject"; "--owner-name" ]
                    |> List.tryFind (fun flag -> not (Map.containsKey flag values))
                with
                | Some flag -> Error(UsageError.MissingOption flag)
                | None ->
                    let issuer, subject = values["--owner-issuer"], values["--owner-subject"]

                    let profile =
                        {
                            ActorProfileInput.DisplayName = values["--owner-name"]
                            DigestEmail = None
                            Contact = Map.tryFind "--owner-contact" values
                        }

                    match PrincipalKey.human issuer subject, ActorProfile.parse profile with
                    | Ok principal, Ok _ ->
                        Ok(
                            Initialize(
                                zone,
                                {
                                    Principal = principal
                                    Profile = profile
                                    Roles = roles
                                }
                            )
                        )
                    | Error _, _ ->
                        let flag =
                            if PrincipalKey.human issuer "owner" |> Result.isError then
                                "--owner-issuer"
                            else
                                "--owner-subject"

                        Error(UsageError.InvalidValue(flag, values[flag]))
                    | _, Error issues ->
                        let flag =
                            if fst issues.Head = ActorProfileField.Contact then
                                "--owner-contact"
                            else
                                "--owner-name"

                        Error(UsageError.InvalidValue(flag, values[flag])))

    let private recoverOwner arguments =
        let allowed = [ "--owner-issuer"; "--owner-subject"; "--owner-name"; "--actor-id" ]

        let rec options values =
            function
            | [] -> Ok values
            | flag :: raw :: rest when List.contains flag allowed ->
                if raw.StartsWith("--", StringComparison.Ordinal) then
                    Error(UsageError.MissingValue flag)
                elif Map.containsKey flag values then
                    Error(UsageError.DuplicateOption flag)
                else
                    options (Map.add flag raw values) rest
            | [ flag ] when List.contains flag allowed -> Error(UsageError.MissingValue flag)
            | flag :: _ -> Error(UsageError.UnknownOption(flag, None))

        options Map.empty arguments
        |> Result.bind (fun values ->
            match Map.tryFind "--owner-issuer" values, Map.tryFind "--owner-subject" values with
            | None, _ -> Error(UsageError.MissingOption "--owner-issuer")
            | _, None -> Error(UsageError.MissingOption "--owner-subject")
            | Some issuer, Some subject ->
                let name = Map.tryFind "--owner-name" values

                let actor =
                    Map.tryFind "--actor-id" values
                    |> Option.map (fun raw ->
                        match Guid.TryParseExact(raw, "D") with
                        | true, value -> ActorId.create value
                        | _ -> None)

                match PrincipalKey.human issuer subject, actor with
                | Ok principal, (None | Some(Some _)) ->
                    Ok(
                        RecoverOwner
                            {
                                Principal = principal
                                ActorId = Option.flatten actor
                                Name = name
                            }
                    )
                | _ ->
                    Error(UsageError.InvalidValue("--owner-issuer/--owner-subject/--actor-id", "")))

    let private verify arguments =
        match arguments with
        | [] -> Ok(Verify None)
        | [ "--checkpoint"; path ] -> checkpoint path |> Result.map (Some >> Verify)
        | _ -> Error(UsageProblem(UsageError.NotApplicable("arguments", "command")))

    let private generation (raw: string) =
        match Int64.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, value when value >= 0L && value <= SafeInteger.Maximum -> Some value
        | _ -> None

    let private pauseId (raw: string) =
        match Guid.TryParseExact(raw, "D") with
        | true, value when value <> Guid.Empty -> Some value
        | _ -> None

    let private handle generationText idText =
        generation generationText
        |> Option.bind (fun generation ->
            pauseId idText |> Option.bind (MaintenanceHandle.create generation))

    let private maintenance arguments =
        let invalid =
            Error(
                UsageError.MissingOperand
                    "acquire GENERATION PAUSE_ID | resume GENERATION PAUSE_ID | takeover GENERATION PAUSE_ID NEW_PAUSE_ID"
            )

        match arguments with
        | [ "acquire"; expected; id ] ->
            match generation expected, pauseId id with
            | Some expected, Some id -> Ok(Maintenance(MaintenanceChange.Acquire(expected, id)))
            | _ -> invalid
        | [ "resume"; generation; id ] ->
            handle generation id
            |> Option.map (MaintenanceChange.Resume >> Maintenance >> Ok)
            |> Option.defaultValue invalid
        | [ "takeover"; generation; id; replacement ] ->
            match handle generation id, pauseId replacement with
            | Some handle, Some replacement ->
                Ok(Maintenance(MaintenanceChange.Takeover(handle, replacement)))
            | _ -> invalid
        | _ -> invalid

    let private checkpointCommand create arguments =
        match arguments with
        | [ "--checkpoint"; path; "--pause-generation"; generation; "--pause-id"; id ] ->
            match handle generation id with
            | Some handle ->
                checkpoint path |> Result.map (fun checkpoint -> create (checkpoint, handle))
            | None -> Error(UsageProblem(UsageError.InvalidValue("--pause-id", id)))
        | _ ->
            Error(
                UsageProblem(
                    UsageError.MissingOperand
                        "--checkpoint FILE --pause-generation GENERATION --pause-id PAUSE_ID"
                )
            )

    let private parse name arguments =
        let usage result = Result.mapError UsageProblem result

        match name with
        | CommandName.Help -> noArguments Help arguments |> usage
        | CommandName.Version -> noArguments Version arguments |> usage
        | CommandName.Initialize -> initialize arguments |> usage
        | CommandName.RecoverOwner -> recoverOwner arguments |> usage
        | CommandName.Head -> noArguments Head arguments |> usage
        | CommandName.Status -> noArguments Status arguments |> usage
        | CommandName.Maintenance -> maintenance arguments |> usage
        | CommandName.Verify -> verify arguments
        | CommandName.Rebuild -> checkpointCommand Rebuild arguments
        | CommandName.AdmitRestoredInstallation ->
            checkpointCommand AdmitRestoredInstallation arguments

    let command arguments =
        match arguments with
        | [] ->
            Error(
                CommandInputFailure.Usage
                    {
                        Issue = UsageError.MissingOperand "COMMAND"
                        Usage =
                            "claimcore-server serve"
                            :: (CommandName.all |> List.map CommandName.synopsis)
                    }
            )
        | first :: rest ->
            match CommandName.parse first with
            | None -> Error(CommandInputFailure.Operator OperatorFault.InvocationInvalid)
            | Some name ->
                parse name rest
                |> Result.mapError (function
                    | OperatorProblem fault -> CommandInputFailure.Operator fault
                    | UsageProblem(UsageError.InvalidValue _ | UsageError.InvalidChoice _) when
                        name = CommandName.Initialize
                        ->
                        CommandInputFailure.Operator OperatorFault.InitialOwnerInvalid
                    | UsageProblem(UsageError.InvalidValue _) when name = CommandName.RecoverOwner ->
                        CommandInputFailure.Operator OperatorFault.OwnerRecoveryRefused
                    | UsageProblem(UsageError.UnknownOption _ | UsageError.ExtraOperand _ | UsageError.UnknownTopic _) ->
                        CommandInputFailure.Operator OperatorFault.InvocationInvalid
                    | UsageProblem issue ->
                        CommandInputFailure.Usage
                            {
                                Issue = issue
                                Usage = [ CommandName.synopsis name ]
                            })

    let connection () =
        match
            Environment.GetEnvironmentVariable "CLAIMCORE_ADMIN_CONNECTION_FILE"
            |> Option.ofObj
        with
        | None -> Error OperatorFault.ConnectionFileRequired
        | Some path when String.IsNullOrWhiteSpace path ->
            Error OperatorFault.ConnectionFileRequired
        | Some path ->
            match PrivateFile.read 16384 path with
            | Error _ -> Error OperatorFault.ConnectionFileRefused
            | Ok bytes ->
                try
                    try
                        let value = UTF8Encoding(false, true).GetString(bytes).Trim()

                        if String.IsNullOrWhiteSpace value then
                            Error OperatorFault.ConnectionFileInvalid
                        else
                            Ok value
                    with :? DecoderFallbackException ->
                        Error OperatorFault.ConnectionFileInvalid
                finally
                    CryptographicOperations.ZeroMemory bytes
