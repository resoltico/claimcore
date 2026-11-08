namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

[<RequireQualifiedAccess>]
type CliDescription =
    | Summary
    | Operation of string list

[<RequireQualifiedAccess>]
type CliSchema =
    | Named of string
    | Request of string
    | Response of string

[<RequireQualifiedAccess>]
type RecoveryAttemptAction = | Check

[<RequireQualifiedAccess>]
type RecoveryAction =
    | List
    | Attempt of RecoveryAttemptAction * System.Guid

type RecoveryInvocation =
    {
        Action: RecoveryAction
        Text: bool
        Language: string
    }

[<RequireQualifiedAccess>]
type CliInvocation =
    | Help of topic: CliTopic * language: string
    | CommandHelp of words: string list * language: string
    | Version of json: bool
    | Describe of selector: CliDescription * text: bool * language: string
    | Schema of selection: CliSchema
    | Call
    | Session
    | Shell
    | Recovery of RecoveryInvocation
    | Configure of Map<CliSetting, string> option
    | Verb of string list

module CliInvocations =
    let private options arguments =
        let rec collect operands format language =
            function
            | [] -> Ok(List.rev operands, format, language)
            | "--format" :: value :: tail when
                format = None && List.contains value [ "json"; "text" ]
                ->
                collect operands (Some value) language tail
            | "--language" :: value :: tail when
                language = None && List.contains value LocalizationPolicy.languages
                ->
                collect operands format (Some value) tail
            | value :: _ when value.StartsWith("-", System.StringComparison.Ordinal) ->
                Error(UsageError.UnknownOption(value, None))
            | value :: tail -> collect (value :: operands) format language tail

        let normalized =
            arguments
            |> List.collect (fun (argument: string) ->
                if argument.StartsWith("--language=", System.StringComparison.Ordinal) then
                    [ "--language"; argument.Substring(11) ]
                elif argument.StartsWith("--format=", System.StringComparison.Ordinal) then
                    [ "--format"; argument.Substring(9) ]
                else
                    [ argument ])

        collect [] None None normalized

    let private help topic language =
        CliTopic.parse topic
        |> Option.map (fun topic -> CliInvocation.Help(topic, language))
        |> Option.map Ok
        |> Option.defaultValue (Error(UsageError.UnknownTopic topic))

    let private commandHelp (words: string list) language =
        let matches =
            CommandGrammar.operations
            |> List.filter (fun entry ->
                List.truncate words.Length (CommandGrammar.words entry) = words)

        if List.isEmpty words || List.isEmpty matches then
            Error(UsageError.UnknownCommand(String.concat " " words, []))
        else
            Ok(CliInvocation.CommandHelp(words, language))

    let private admitHelp tail =
        options tail
        |> Result.bind (fun (operands, format, language) ->
            let language = Option.defaultValue "en" language

            match operands, format with
            | [], None -> help "overview" language
            | [ topic ], None when CliTopic.parse topic |> Option.isSome -> help topic language
            | words, None -> commandHelp words language
            | _ -> Error(UsageError.NotApplicable("options", "command")))

    let private describeSelector operands =
        match operands with
        | [] -> Ok CliDescription.Summary
        | words when
            CommandGrammar.operations
            |> List.exists (fun entry -> CommandGrammar.words entry = words)
            ->
            Ok(CliDescription.Operation words)
        | _ -> Error(UsageError.UnknownCommand(String.concat " " operands, []))

    let private schemaSelector operands =
        match operands with
        | [ name ] -> Ok(CliSchema.Named name)
        | [ name; direction ] ->
            match Api.tryFind name, direction with
            | Some endpoint, "request" -> Ok(CliSchema.Request(Api.id endpoint))
            | Some endpoint, "response" -> Ok(CliSchema.Response(Api.id endpoint))
            | _ -> Error(UsageError.UnknownCommand(String.concat " " operands, []))
        | _ -> Error(UsageError.UnknownCommand(String.concat " " operands, []))

    let private admitDescribe tail =
        options tail
        |> Result.bind (fun (operands, format, language) ->
            if language.IsSome && format = Some "json" then
                Error(UsageError.NotApplicable("options", "command"))
            else
                describeSelector operands
                |> Result.map (fun selector ->
                    CliInvocation.Describe(
                        selector,
                        (format <> Some "json"),
                        Option.defaultValue "en" language
                    )))

    let private admitVersion =
        function
        | [] -> Ok(CliInvocation.Version false)
        | [ "--json" ] -> Ok(CliInvocation.Version true)
        | _ -> Error(UsageError.NotApplicable("arguments", "command"))

    let private admitSchema arguments =
        if
            arguments
            |> List.exists (fun (item: string) ->
                item.StartsWith("-", System.StringComparison.Ordinal))
        then
            Error(UsageError.NotApplicable("options", "command"))
        else
            schemaSelector arguments |> Result.map CliInvocation.Schema

    let private noArguments invocation tail =
        if List.isEmpty tail then
            Ok invocation
        else
            Error(UsageError.NotApplicable("arguments", "command"))

    let private admitRecovery arguments =
        options arguments
        |> Result.bind (fun (operands, format, language) ->
            let admitted action =
                Ok(
                    CliInvocation.Recovery
                        {
                            Action = action
                            Text = format <> Some "json"
                            Language = Option.defaultValue "en" language
                        }
                )

            match operands with
            | [ "list" ] -> admitted RecoveryAction.List
            | [ "check"; raw ] ->
                match System.Guid.TryParseExact(raw, "D") with
                | true, attempt when attempt <> System.Guid.Empty ->
                    admitted (RecoveryAction.Attempt(RecoveryAttemptAction.Check, attempt))
                | _ -> Error(UsageError.InvalidValue("ATTEMPT_ID", raw))
            | _ -> Error(UsageError.MissingOperand "list | check ATTEMPT_ID"))

    let private admitCommand command tail =
        match tail with
        | [ flag ] when List.contains flag CliCommands.aliases ->
            Ok(CliInvocation.Help(CliCommands.commandTopic command, "en"))
        | _ ->
            match command with
            | CliCommand.Help -> admitHelp tail
            | CliCommand.Version -> admitVersion tail
            | CliCommand.Describe -> admitDescribe tail
            | CliCommand.Schema -> admitSchema tail
            | CliCommand.Call -> noArguments CliInvocation.Call tail
            | CliCommand.Recovery -> admitRecovery tail
            | CliCommand.Configure -> CliConfigure.admit tail |> Result.map CliInvocation.Configure
            | CliCommand.Shell -> noArguments CliInvocation.Shell tail
            | CliCommand.Session -> noArguments CliInvocation.Session tail

    let private wantsCommandHelp (arguments: string list) =
        let helpRequested =
            arguments
            |> List.exists (fun argument -> List.contains argument CliCommands.aliases)

        let commandPath =
            arguments
            |> List.takeWhile (fun word ->
                not (word.StartsWith("-", System.StringComparison.Ordinal)))

        let family =
            CommandGrammar.operations
            |> List.exists (fun entry ->
                let words = CommandGrammar.words entry
                not commandPath.IsEmpty && List.truncate commandPath.Length words = commandPath)

        let complete =
            CommandGrammar.operations
            |> List.exists (fun operation -> CommandGrammar.words operation = commandPath)

        family && (helpRequested || commandPath.Length = 1 && not complete)

    let admit (arguments: string list) =
        match arguments with
        | [] -> Ok(CliInvocation.Help(CliTopic.Help HelpTopic.Overview, "en"))
        | [ flag ] when List.contains flag CliCommands.aliases ->
            Ok(CliInvocation.Help(CliTopic.Help HelpTopic.Overview, "en"))
        | flag :: tail when List.contains flag CliCommands.aliases -> admitHelp tail
        | head :: tail ->
            match CliCommand.parse head with
            | Some command -> admitCommand command tail
            | None when wantsCommandHelp arguments ->
                options (
                    arguments
                    |> List.filter (fun argument ->
                        not (List.contains argument CliCommands.aliases))
                )
                |> Result.bind (fun (words, format, language) ->
                    if format.IsSome then
                        Error(UsageError.NotApplicable("--format", "help"))
                    else
                        commandHelp words (Option.defaultValue "en" language))
            | None -> Ok(CliInvocation.Verb arguments)

    /// Defaults belong only to human verbs; explicit arguments and structured modes retain their bytes.
    let internal principalIssuer profile invocation =
        match invocation with
        | CliInvocation.Verb words ->
            let explicit =
                words
                |> List.exists (fun (word: string) ->
                    word = "--issuer"
                    || word.StartsWith("--issuer=", System.StringComparison.Ordinal))

            let acceptsIssuer =
                CommandGrammar.tryOperation words
                |> Option.exists (fun operation ->
                    let inputs, _ = CommandGrammar.derive operation

                    inputs
                    |> List.exists (fun input ->
                        input.Name = InteractionInput.Target InputTarget.PrincipalIssuer))

            match explicit, acceptsIssuer, Map.tryFind CliSetting.Issuer profile with
            | false, true, Some issuer -> CliInvocation.Verb(words @ [ "--issuer"; issuer ])
            | _ -> invocation
        | _ -> invocation

    /// Resolve profile/environment defaults only at the executable and shell boundaries.
    let admitConfigured arguments =
        let explicit =
            arguments
            |> List.exists (fun (argument: string) ->
                argument = "--language"
                || argument.StartsWith("--language=", System.StringComparison.Ordinal))

        admit arguments
        |> Result.map (fun invocation ->
            if explicit then
                invocation
            else
                match invocation with
                | CliInvocation.Help(topic, _) ->
                    CliInvocation.Help(topic, CliLanguage.effective ())
                | CliInvocation.CommandHelp(words, _) ->
                    CliInvocation.CommandHelp(words, CliLanguage.effective ())
                | CliInvocation.Describe(selector, true, _) ->
                    CliInvocation.Describe(selector, true, CliLanguage.effective ())
                | CliInvocation.Recovery selected ->
                    CliInvocation.Recovery
                        { selected with
                            Language = CliLanguage.effective ()
                        }
                | CliInvocation.Verb words when CommandGrammar.tryOperation words |> Option.isSome ->
                    CliInvocation.Verb(words @ [ "--language"; CliLanguage.effective () ])
                | _ -> invocation)
        |> Result.map (fun invocation ->
            match invocation with
            | CliInvocation.Verb _ ->
                let profile =
                    CliProfile.location CliLanguage.environment
                    |> Result.bind CliProfile.read
                    |> Result.defaultValue Map.empty

                principalIssuer profile invocation
            | _ -> invocation)

    let usageLanguage arguments =
        let separate =
            arguments
            |> List.pairwise
            |> List.choose (fun (flag, value) ->
                if flag = "--language" && List.contains value LocalizationPolicy.languages then
                    Some value
                else
                    None)

        let assigned =
            arguments
            |> List.choose (fun (argument: string) ->
                if argument.StartsWith("--language=", System.StringComparison.Ordinal) then
                    let value = argument.Substring(11)

                    if List.contains value LocalizationPolicy.languages then
                        Some value
                    else
                        None
                else
                    None)

        match separate @ assigned with
        | [ language ] -> language
        | _ -> CliLanguage.effective ()
