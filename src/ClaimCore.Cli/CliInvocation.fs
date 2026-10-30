namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol
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
type CliInvocation =
    | Help of topic: CliTopic * language: string
    | Version of json: bool
    | Describe of selector: CliDescription * text: bool * language: string
    | Schema of selection: CliSchema
    | Call
    | Session
    | Shell
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
                Error(UsageError.NotApplicable("options", "command"))
            | value :: tail -> collect (value :: operands) format language tail

        collect [] None None arguments

    let private help topic language =
        CliTopic.parse topic
        |> Option.map (fun topic -> CliInvocation.Help(topic, language))
        |> Option.map Ok
        |> Option.defaultValue (Error(UsageError.UnknownTopic topic))

    let private admitHelp tail =
        options tail
        |> Result.bind (fun (operands, format, language) ->
            match operands, format with
            | [], None -> help "overview" (defaultArg language "en")
            | [ topic ], None -> help topic (defaultArg language "en")
            | _ -> Error(UsageError.NotApplicable("options", "command")))

    let private describeSelector operands =
        match operands with
        | [] -> Ok CliDescription.Summary
        | words when
            OperationCatalog.all
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
                        defaultArg language "en"
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
            | CliCommand.Configure -> CliConfigure.admit tail |> Result.map CliInvocation.Configure
            | CliCommand.Shell -> noArguments CliInvocation.Shell tail
            | CliCommand.Session -> noArguments CliInvocation.Session tail

    let admit arguments =
        match arguments with
        | [] -> Ok(CliInvocation.Help(CliTopic.Help HelpTopic.Overview, "en"))
        | [ flag ] when List.contains flag CliCommands.aliases ->
            Ok(CliInvocation.Help(CliTopic.Help HelpTopic.Overview, "en"))
        | head :: tail ->
            match CliCommand.parse head with
            | Some command -> admitCommand command tail
            | None -> Ok(CliInvocation.Verb arguments)

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
        | _ -> "en"
