namespace ClaimCore.Database

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

type Command =
    | Initialize of timeZone: string * owner: InitialOwner
    | Verify of LedgerPosition option
    | Rebuild of LedgerPosition
    | InvalidateRestoredSessions
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
    | InvalidateRestoredSessions
    | Head
    | Status
    | Maintenance
    | Help
    | Version

module Command =
    [<Literal>]
    let ProgramName = "claimcore-database"

    let supportedExitCodes =
        [
            ExitCode.Success
            ExitCode.Rejected
            ExitCode.Failed
            ExitCode.OutcomeUnknown
            ExitCode.Diverged
        ]

    let name =
        function
        | Initialize _ -> CommandName.Initialize
        | Verify _ -> CommandName.Verify
        | Rebuild _ -> CommandName.Rebuild
        | InvalidateRestoredSessions -> CommandName.InvalidateRestoredSessions
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
        | CommandName.Verify -> [ CommandArgument.Optional("--expect", "SEQUENCE:HASH") ]
        | CommandName.Rebuild -> [ CommandArgument.Operand "--expect SEQUENCE:HASH" ]
        | CommandName.Maintenance -> [ CommandArgument.Operand "on|off" ]
        | CommandName.InvalidateRestoredSessions -> []
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

module Inputs =
    let private expected (text: string) =
        let parts = text.Split(':')
        let mutable sequence = 0L

        if
            parts.Length = 2
            && Int64.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, &sequence)
            && sequence >= 0L
            && sequence.ToString(CultureInfo.InvariantCulture) = parts[0]
            && parts[1].Length = 64
            && parts[1]
               |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
        then
            Sequence.create sequence
            |> Option.bind (fun sequence ->
                Convert.FromHexString(parts[1])
                |> EntryHash.ofBytes
                |> Option.map (LedgerPosition.create sequence))
        else
            None

    let private noArguments command arguments =
        if List.isEmpty arguments then
            Ok command
        else
            Error(UsageError.NotApplicable("arguments", "command"))

    let private initialize arguments =
        let rec options values roles =
            function
            | [] -> Ok(values, List.rev roles)
            | "--owner-role" :: raw :: rest ->
                match Role.parse raw with
                | Some role when Authority.initialOwnerRole role && not (List.contains role roles) ->
                    options values (role :: roles) rest
                | _ ->
                    Error(
                        UsageError.InvalidChoice(
                            "--owner-role",
                            raw,
                            Authority.initialOwnerRoles |> List.map Role.token
                        )
                    )
            | flag :: raw :: rest when
                List.contains
                    flag
                    [ "--owner-issuer"; "--owner-subject"; "--owner-name"; "--owner-contact" ]
                ->
                if raw.StartsWith("--", StringComparison.Ordinal) then
                    Error(UsageError.MissingValue flag)
                elif Map.containsKey flag values then
                    Error(UsageError.DuplicateOption flag)
                else
                    options (Map.add flag raw values) roles rest
            | [ flag ] when flag.StartsWith("--owner-", StringComparison.Ordinal) ->
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
        | [ "--expect"; value ] ->
            expected value
            |> Option.map (Some >> Verify >> Ok)
            |> Option.defaultValue (Error(UsageError.InvalidValue("--expect", value)))
        | _ -> Error(UsageError.NotApplicable("arguments", "command"))

    let private maintenance arguments =
        match arguments with
        | [ "on" ] -> Ok(Maintenance MaintenanceChange.On)
        | [ "off" ] -> Ok(Maintenance MaintenanceChange.Off)
        | _ -> Error(UsageError.MissingOperand "on|off")

    let private rebuild arguments =
        match arguments with
        | [ "--expect"; value ] ->
            expected value
            |> Option.map (Rebuild >> Ok)
            |> Option.defaultValue (Error(UsageError.InvalidValue("--expect", value)))
        | _ -> Error(UsageError.MissingOperand "--expect SEQUENCE:HASH")

    let private parse name arguments =
        match name with
        | CommandName.Help -> noArguments Help arguments
        | CommandName.Version -> noArguments Version arguments
        | CommandName.Initialize -> initialize arguments
        | CommandName.Head -> noArguments Head arguments
        | CommandName.Status -> noArguments Status arguments
        | CommandName.Maintenance -> maintenance arguments
        | CommandName.Verify -> verify arguments
        | CommandName.Rebuild -> rebuild arguments
        | CommandName.InvalidateRestoredSessions -> noArguments InvalidateRestoredSessions arguments

    let command arguments =
        match arguments with
        | [] -> Ok Help
        | first :: rest ->
            match CommandName.parse first with
            | None ->
                Error
                    {
                        Issue = UsageError.UnknownCommand(first, [])
                        Usage = CommandName.all |> List.map CommandName.synopsis
                    }
            | Some name ->
                parse name rest
                |> Result.mapError (fun issue ->
                    {
                        Issue = issue
                        Usage = [ CommandName.synopsis name ]
                    })

    let private bytes setting maximum =
        Environment.GetEnvironmentVariable setting
        |> Option.ofObj
        |> Option.map (fun path ->
            PrivateFile.read maximum path
            |> Result.mapError (fun _ -> "Private input was refused."))
        |> Option.defaultValue (Error "Required private input is absent.")
        |> Result.defaultWith invalidOp

    let connection () =
        let value = bytes "CLAIMCORE_ADMIN_CONNECTION_FILE" 16384

        try
            UTF8Encoding(false, true).GetString(value).Trim()
        finally
            CryptographicOperations.ZeroMemory value
