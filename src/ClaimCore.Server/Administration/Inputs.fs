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
open ClaimCore.Postgres
open ClaimCore.HostSecurity
open ClaimCore.RecordFormat

type Command =
    | Initialize of timeZone: string * owner: InitialOwner
    | Verify of InstallationCheckpoint option
    | Rebuild of InstallationCheckpoint
    | AdmitRestoredInstallation of InstallationCheckpoint
    | Head
    | Status
    | Maintenance of MaintenanceChange
    | Help
    | Version

[<RequireQualifiedAccess>]
type CommandName =
    | Initialize
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
        | CommandName.Verify -> [ CommandArgument.Optional("--checkpoint", "FILE") ]
        | CommandName.Rebuild -> [ CommandArgument.Operand "--checkpoint FILE" ]
        | CommandName.Maintenance -> [ CommandArgument.Operand "on|off" ]
        | CommandName.AdmitRestoredInstallation -> [ CommandArgument.Operand "--checkpoint FILE" ]
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
                | Some role when People.initialOwnerRole role && not (List.contains role roles) ->
                    options values (role :: roles) rest
                | _ ->
                    Error(
                        UsageError.InvalidChoice(
                            "--owner-role",
                            raw,
                            People.initialOwnerRoles |> List.map Role.token
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

    let private verify arguments =
        match arguments with
        | [] -> Ok(Verify None)
        | [ "--checkpoint"; path ] -> checkpoint path |> Result.map (Some >> Verify)
        | _ -> Error(UsageProblem(UsageError.NotApplicable("arguments", "command")))

    let private maintenance arguments =
        match arguments with
        | [ "on" ] -> Ok(Maintenance MaintenanceChange.On)
        | [ "off" ] -> Ok(Maintenance MaintenanceChange.Off)
        | _ -> Error(UsageError.MissingOperand "on|off")

    let private checkpointCommand create arguments =
        match arguments with
        | [ "--checkpoint"; path ] -> checkpoint path |> Result.map create
        | _ -> Error(UsageProblem(UsageError.MissingOperand "--checkpoint FILE"))

    let private parse name arguments =
        let usage result = Result.mapError UsageProblem result

        match name with
        | CommandName.Help -> noArguments Help arguments |> usage
        | CommandName.Version -> noArguments Version arguments |> usage
        | CommandName.Initialize -> initialize arguments |> usage
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
                    | UsageProblem(UsageError.InvalidValue _ | UsageError.InvalidChoice _) ->
                        CommandInputFailure.Operator OperatorFault.InitialOwnerInvalid
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
