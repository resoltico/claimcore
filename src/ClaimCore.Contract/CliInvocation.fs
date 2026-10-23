namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

[<RequireQualifiedAccess>]
type CliDescription =
    | Summary
    | Fields of string option
    | Commands of string option
    | Authority of string option
    | Endpoints of Endpoint option
    | Diagnostics

[<RequireQualifiedAccess>]
type CliSchema =
    | Named of string
    | Request of Endpoint
    | Response of Endpoint

[<RequireQualifiedAccess>]
type CliInvocation =
    | Help of topic: string * language: string
    | Version of json: bool
    | Describe of selector: CliDescription * text: bool * language: string
    | Schema of selection: CliSchema
    | Call
    | Session

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
        if List.contains topic CliHelp.topics then
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

    let private descriptions =
        [
            "fields", CliDescription.Fields
            "commands", CliDescription.Commands
            "authority", CliDescription.Authority
        ]

    let private describeSelector =
        function
        | []
        | [ "summary" ] -> Ok CliDescription.Summary
        | [ "diagnostics" ] -> Ok CliDescription.Diagnostics
        | [ "endpoints" ] -> Ok(CliDescription.Endpoints None)
        | [ "endpoints"; name ] ->
            match Endpoint.tryParse name with
            | Some endpoint -> Ok(CliDescription.Endpoints(Some endpoint))
            | None -> Error CliUsageIssue.Selector
        | category :: tail ->
            match List.tryFind (fst >> (=) category) descriptions, tail with
            | Some(_, create), [] -> Ok(create None)
            | Some(_, create), [ name ] -> Ok(create (Some name))
            | _ -> Error CliUsageIssue.Selector

    let private schemaSelector =
        function
        | [ name ] -> Ok(CliSchema.Named name)
        | [ name; direction ] ->
            match Endpoint.tryParse name, direction with
            | Some endpoint, "request" -> Ok(CliSchema.Request endpoint)
            | Some endpoint, "response" -> Ok(CliSchema.Response endpoint)
            | _ -> Error CliUsageIssue.Selector
        | _ -> Error CliUsageIssue.Selector

    let private admitDescribe tail =
        options tail
        |> Result.bind (fun (operands, format, language) ->
            if language.IsSome && format <> Some "text" then
                Error CliUsageIssue.Options
            else
                describeSelector operands
                |> Result.map (fun selector ->
                    CliInvocation.Describe(
                        selector,
                        (format = Some "text"),
                        defaultArg language "en"
                    )))

    let private alias command flag =
        List.contains command CliHelp.commands && List.contains flag CliHelp.aliases

    let admit arguments =
        match arguments with
        | [] -> help "overview" "en"
        | [ flag ] when List.contains flag CliHelp.aliases -> help "overview" "en"
        | [ command; flag ] when alias command flag -> help command "en"
        | [ "call" ] -> Ok CliInvocation.Call
        | [ "session" ] -> Ok CliInvocation.Session
        | [ "version" ] -> Ok(CliInvocation.Version false)
        | [ "version"; "--json" ] -> Ok(CliInvocation.Version true)
        | "help" :: tail -> admitHelp tail
        | "describe" :: tail -> admitDescribe tail
        | "schema" :: tail when
            not (
                List.exists
                    (fun (item: string) -> item.StartsWith("-", System.StringComparison.Ordinal))
                    tail
            )
            ->
            schemaSelector tail |> Result.map CliInvocation.Schema
        | _ -> Error CliUsageIssue.Invocation

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
