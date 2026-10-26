namespace ClaimCore.Cli

open ClaimCore.Contract
open ClaimCore.Json
open ClaimCore.Contract.Protocol

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
    | Help of topic: string * language: string
    | Version of json: bool
    | Describe of selector: CliDescription * text: bool * language: string
    | Schema of selection: CliSchema
    | Call
    | Session
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
                Error CliUsageIssue.Options
            | value :: tail -> collect (value :: operands) format language tail

        collect [] None None arguments

    let private help topic language =
        if List.contains topic CliCommands.topics then
            Ok(CliInvocation.Help(topic, language))
        else
            Error CliUsageIssue.Topic

    let private admitHelp tail =
        options tail
        |> Result.bind (fun (operands, format, language) ->
            match operands, format with
            | [], None -> help "overview" (defaultArg language "en")
            | [ topic ], None -> help topic (defaultArg language "en")
            | _ -> Error CliUsageIssue.Options)

    let private describeSelector operands =
        match operands with
        | [] -> Ok CliDescription.Summary
        | words when
            OperationCatalog.all
            |> List.exists (fun entry -> OperationCatalog.words entry = words)
            ->
            Ok(CliDescription.Operation words)
        | _ -> Error CliUsageIssue.Selector

    let private schemaSelector =
        function
        | [ name ] -> Ok(CliSchema.Named name)
        | [ name; direction ] ->
            match Api.tryFind name, direction with
            | Some endpoint, "request" -> Ok(CliSchema.Request(Api.id endpoint))
            | Some endpoint, "response" -> Ok(CliSchema.Response(Api.id endpoint))
            | _ -> Error CliUsageIssue.Selector
        | _ -> Error CliUsageIssue.Selector

    let private admitDescribe tail =
        options tail
        |> Result.bind (fun (operands, format, language) ->
            if language.IsSome && format = Some "json" then
                Error CliUsageIssue.Options
            else
                describeSelector operands
                |> Result.map (fun selector ->
                    CliInvocation.Describe(
                        selector,
                        (format <> Some "json"),
                        defaultArg language "en"
                    )))

    let private alias command flag =
        List.contains command CliCommands.commands
        && List.contains flag CliCommands.aliases

    let private admitVersion =
        function
        | [] -> Ok(CliInvocation.Version false)
        | [ "--json" ] -> Ok(CliInvocation.Version true)
        | _ -> Error CliUsageIssue.Invocation

    let private admitSchema arguments =
        if
            arguments
            |> List.exists (fun (item: string) ->
                item.StartsWith("-", System.StringComparison.Ordinal))
        then
            Error CliUsageIssue.Options
        else
            schemaSelector arguments |> Result.map CliInvocation.Schema

    let admit arguments =
        match arguments with
        | [] -> help "overview" "en"
        | [ flag ] when List.contains flag CliCommands.aliases -> help "overview" "en"
        | [ command; flag ] when alias command flag -> help command "en"
        | [ "call" ] -> Ok CliInvocation.Call
        | [ "session" ] -> Ok CliInvocation.Session
        | "version" :: tail -> admitVersion tail
        | "help" :: tail -> admitHelp tail
        | "describe" :: tail -> admitDescribe tail
        | "schema" :: tail -> admitSchema tail
        | family :: _ when List.contains family [ "case"; "authority"; "operation"; "context" ] ->
            Ok(CliInvocation.Verb arguments)
        | head :: _ when List.contains head CliCommands.commands -> Error CliUsageIssue.Invocation
        | _ -> Ok(CliInvocation.Verb arguments)

    let usageLanguage arguments =
        arguments
        |> List.pairwise
        |> List.choose (fun (flag, value) ->
            if flag = "--language" && List.contains value LocalizationPolicy.languages then
                Some value
            else
                None)
        |> function
            | [ language ] -> language
            | _ -> "en"
