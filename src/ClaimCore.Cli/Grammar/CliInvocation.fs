namespace ClaimCore.Cli

open ClaimCore.CommandLine

open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

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
        Rendering: RenderingContext
    }

    member this.Language = this.Rendering.Language

[<RequireQualifiedAccess>]
type CliInvocation =
    | Help of topic: CliTopic * rendering: RenderingContext * json: bool
    | CommandHelp of words: string list * rendering: RenderingContext * json: bool
    | Version of json: bool
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
            | "--display-locale" :: value :: tail ->
                if List.contains value LocalizationPolicy.displayLocales then
                    collect operands format language tail
                else
                    Error(
                        UsageError.InvalidChoice(
                            "--display-locale",
                            value,
                            LocalizationPolicy.displayLocales
                        )
                    )
            | [ "--display-locale" ] -> Error(UsageError.MissingValue "--display-locale")
            | value :: _ when value.StartsWith("-", System.StringComparison.Ordinal) ->
                Error(UsageError.UnknownOption(value, None))
            | value :: tail -> collect (value :: operands) format language tail

        let normalized =
            arguments
            |> List.collect (fun (argument: string) ->
                if argument.StartsWith("--language=", System.StringComparison.Ordinal) then
                    [ "--language"; argument.Substring(11) ]
                elif argument.StartsWith("--display-locale=", System.StringComparison.Ordinal) then
                    [ "--display-locale"; argument.Substring(17) ]
                elif argument.StartsWith("--format=", System.StringComparison.Ordinal) then
                    [ "--format"; argument.Substring(9) ]
                else
                    [ argument ])

        if (normalized |> List.filter ((=) "--display-locale") |> List.length) > 1 then
            Error(UsageError.DuplicateOption "--display-locale")
        else
            collect [] None None normalized

    let private help topic language json =
        CliTopic.parse topic
        |> Option.map (fun topic ->
            CliInvocation.Help(topic, RenderingContext.forLanguage language, json))
        |> Option.map Ok
        |> Option.defaultValue (Error(UsageError.UnknownTopic topic))

    let private commandHelp (words: string list) language json =
        let matches =
            CommandGrammar.operations
            |> List.filter (fun entry ->
                List.truncate words.Length (CommandGrammar.words entry) = words)

        if List.isEmpty words || List.isEmpty matches then
            Error(UsageError.UnknownCommand(String.concat " " words, []))
        else
            Ok(CliInvocation.CommandHelp(words, RenderingContext.forLanguage language, json))

    let private admitHelp tail =
        options tail
        |> Result.bind (fun (operands, format, language) ->
            let language = Option.defaultValue "en" language

            let json = format = Some "json"

            match operands with
            | [] -> help "overview" language json
            | [ topic ] when CliTopic.parse topic |> Option.isSome -> help topic language json
            | words -> commandHelp words language json)

    let private schemaSelector operands =
        match operands with
        | [] -> Error(UsageError.MissingOperand "SCHEMA")
        | [ name ] -> Ok(CliSchema.Named name)
        | [ name; direction ] ->
            match Api.tryFind name, direction with
            | Some endpoint, "request" -> Ok(CliSchema.Request(Api.id endpoint))
            | Some endpoint, "response" -> Ok(CliSchema.Response(Api.id endpoint))
            | _ -> Error(UsageError.UnknownCommand(String.concat " " operands, []))
        | _ -> Error(UsageError.UnknownCommand(String.concat " " operands, []))

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
                            Rendering =
                                RenderingContext.forLanguage (Option.defaultValue "en" language)
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
            Ok(
                CliInvocation.Help(
                    CliCommands.commandTopic command,
                    RenderingContext.forLanguage "en",
                    false
                )
            )
        | _ ->
            match command with
            | CliCommand.Help -> admitHelp tail
            | CliCommand.Version -> admitVersion tail
            | CliCommand.Schema -> admitSchema tail
            | CliCommand.Call -> noArguments CliInvocation.Call tail
            | CliCommand.Recovery -> admitRecovery tail
            | CliCommand.Configure -> CliConfigure.admit tail |> Result.map CliInvocation.Configure
            | CliCommand.Shell -> noArguments CliInvocation.Shell tail
            | CliCommand.Session -> noArguments CliInvocation.Session tail

    let private wantsCommandHelp (arguments: string list) =
        let switches =
            CommandGrammar.tryOperation arguments
            |> Option.map (fun operation ->
                (CommandGrammar.optionGrammar operation).Switches
                @ CommandGrammar.textSourceSwitches operation)
            |> Option.defaultValue CommandGrammar.globalSwitches

        let helpRequested =
            VerbOptions.beforeTerminator arguments
            |> VerbOptions.classify (switches @ CliCommands.aliases)
            |> List.exists (function
                | VerbOptions.Argument.Option(name, VerbOptions.OptionValue.Switch) ->
                    List.contains name CliCommands.aliases
                | VerbOptions.Argument.Operand value -> List.contains value CliCommands.aliases
                | _ -> false)

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
        | [] ->
            Ok(
                CliInvocation.Help(
                    CliTopic.Help HelpTopic.Overview,
                    RenderingContext.forLanguage "en",
                    false
                )
            )
        | [ flag ] when List.contains flag CliCommands.aliases ->
            Ok(
                CliInvocation.Help(
                    CliTopic.Help HelpTopic.Overview,
                    RenderingContext.forLanguage "en",
                    false
                )
            )
        | flag :: tail when List.contains flag CliCommands.aliases -> admitHelp tail
        | head :: tail ->
            // Declared multiword tasks may share a family with an executable command.
            match
                (if CommandGrammar.tryOperation arguments |> Option.isSome then
                     None
                 else
                     CliCommand.parse head)
            with
            | Some command -> admitCommand command tail
            | None when wantsCommandHelp arguments ->
                options (
                    arguments
                    |> List.filter (fun argument ->
                        not (List.contains argument CliCommands.aliases))
                )
                |> Result.bind (fun (words, format, language) ->
                    commandHelp words (Option.defaultValue "en" language) (format = Some "json"))
            | None -> Ok(CliInvocation.Verb arguments)

    /// Defaults belong only to human verbs; explicit arguments and structured modes retain their bytes.
    let internal principalIssuer profile invocation =
        match invocation with
        | CliInvocation.Verb words ->
            let explicit =
                VerbOptions.beforeTerminator words
                |> List.exists (fun (word: string) ->
                    word = "--issuer"
                    || word.StartsWith("--issuer=", System.StringComparison.Ordinal))

            let acceptsIssuer =
                CommandGrammar.tryOperation words
                |> Option.exists (fun operation ->
                    let inputs = CommandGrammar.inputs operation

                    inputs
                    |> List.exists (fun input ->
                        input.Name = TaskInputId.Target InputTarget.PrincipalIssuer))

            match explicit, acceptsIssuer, Map.tryFind CliSetting.Issuer profile with
            | false, true, Some issuer ->
                CliInvocation.Verb(VerbOptions.insertOptions [ "--issuer"; issuer ] words)
            | _ -> invocation
        | _ -> invocation

    let private flagValues flag (arguments: string list) =
        let arguments = VerbOptions.beforeTerminator arguments

        arguments
        |> List.mapi (fun index argument ->
            if argument = flag then
                List.tryItem (index + 1) arguments
            elif argument.StartsWith(flag + "=", System.StringComparison.Ordinal) then
                Some(argument.Substring(flag.Length + 1))
            else
                None)
        |> List.choose id

    let private rendersPreferences =
        function
        | CliInvocation.Help _
        | CliInvocation.CommandHelp _
        | CliInvocation.Recovery _ -> true
        | CliInvocation.Verb words -> CommandGrammar.tryOperation words |> Option.isSome
        | CliInvocation.Version _
        | CliInvocation.Schema _
        | CliInvocation.Call
        | CliInvocation.Session
        | CliInvocation.Shell
        | CliInvocation.Configure _ -> false

    /// Resolve public preferences once at each executable or shell command boundary.
    let admitConfiguredWith environment arguments =
        let locales = flagValues "--display-locale" arguments
        let suppliedLocale = List.tryHead locales

        let optionArguments = VerbOptions.beforeTerminator arguments

        let missingLocale =
            optionArguments
            |> List.mapi (fun index argument ->
                argument = "--display-locale"
                && (List.tryItem (index + 1) optionArguments
                    |> Option.forall (fun value ->
                        value.StartsWith("--", System.StringComparison.Ordinal))))
            |> List.exists id

        if missingLocale then
            Error(UsageError.MissingValue "--display-locale")
        elif locales.Length > 1 then
            Error(UsageError.DuplicateOption "--display-locale")
        else
            admit arguments
            |> Result.bind (fun invocation ->
                if not (rendersPreferences invocation) then
                    Ok invocation
                else
                    let profile =
                        CliProfile.location environment
                        |> Result.bind CliProfile.read
                        |> Result.mapError (fun _ ->
                            UsageError.InvalidValue("profile", "configuration"))

                    profile
                    |> Result.bind (fun profile ->
                        let languages = flagValues "--language" arguments

                        let selected =
                            languages
                            |> List.tryHead
                            |> Option.defaultWith (fun () ->
                                CliLanguage.choose environment profile |> fst)

                        let language =
                            if List.contains selected LocalizationPolicy.languages then
                                selected
                            else
                                "en"

                        CliLanguage.displayLocale environment profile language suppliedLocale
                        |> Result.mapError (fun value ->
                            UsageError.InvalidChoice(
                                "--display-locale",
                                value,
                                LocalizationPolicy.displayLocales
                            ))
                        |> Result.map (fun (context, _) ->
                            let invocation =
                                match invocation with
                                | CliInvocation.Help(topic, _, json) ->
                                    CliInvocation.Help(topic, context, json)
                                | CliInvocation.CommandHelp(words, _, json) ->
                                    CliInvocation.CommandHelp(words, context, json)
                                | CliInvocation.Recovery selected ->
                                    CliInvocation.Recovery { selected with Rendering = context }
                                | CliInvocation.Verb words when
                                    CommandGrammar.tryOperation words |> Option.isSome
                                    ->
                                    let words =
                                        if languages.IsEmpty then
                                            VerbOptions.insertOptions
                                                [ "--language"; context.Language ]
                                                words
                                        else
                                            words

                                    let words =
                                        if locales.IsEmpty then
                                            VerbOptions.insertOptions
                                                [ "--display-locale"; context.DisplayLocale ]
                                                words
                                        else
                                            words

                                    CliInvocation.Verb words
                                | _ -> invocation

                            principalIssuer profile invocation)))

    let admitConfigured arguments =
        admitConfiguredWith CliLanguage.environment arguments

    let usageLanguage arguments =
        let arguments = VerbOptions.beforeTerminator arguments

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
