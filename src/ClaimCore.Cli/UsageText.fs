namespace ClaimCore.Cli

open System
open ClaimCore.Contract

module UsageText =
    let private message language key values =
        MessageCatalogs.render language ("cli.usage." + key) (Map.ofList values)

    // Unrelated arbitrary input is not echoed as a command.
    let private suggestedCommandText command suggestions =
        if List.isEmpty suggestions then "" else command

    let private line language =
        function
        | UsageError.UnknownCommand(command, suggestions) ->
            message
                language
                "unknownCommand"
                [ "command", suggestedCommandText command suggestions ]
        | UsageError.UnknownOption(flag, suggestion) ->
            message language "unknownOption" [ "flag", flag ]
            + (suggestion
               |> Option.map (fun suggestion ->
                   " " + message language "suggestion" [ "suggestion", suggestion ])
               |> Option.defaultValue "")
        | UsageError.MissingOption flag -> message language "missingOption" [ "flag", flag ]
        | UsageError.MissingOperand operand ->
            message language "missingOperand" [ "operand", operand ]
        | UsageError.ExtraOperand operand -> message language "extraOperand" [ "operand", operand ]
        | UsageError.InvalidInteger(flag, value) ->
            message language "invalidInteger" [ "flag", flag; "value", value ]
        | UsageError.InvalidChoice(flag, value, []) ->
            message language "invalidValue" [ "flag", flag; "value", value ]
        | UsageError.InvalidChoice(flag, value, allowed) ->
            message
                language
                "invalidChoice"
                [ "flag", flag; "value", value; "allowed", String.concat "|" allowed ]
        | UsageError.NotApplicable(flag, because) ->
            message language "notApplicable" [ "flag", flag; "because", because ]
        | UsageError.DuplicateOption flag -> message language "duplicateOption" [ "flag", flag ]
        | UsageError.MissingValue flag -> message language "missingValue" [ "flag", flag ]
        | UsageError.RequestTooLarge maximum ->
            message language "requestTooLarge" [ "maximumBytes", string maximum ]

    let render language arguments errors =
        let matched =
            OperationCatalog.all
            |> List.tryFind (fun entry ->
                let words = OperationCatalog.words entry in
                List.truncate words.Length arguments = words)

        let hints =
            match matched with
            | Some entry ->
                [
                    message
                        language
                        "synopsis"
                        [
                            "synopsis",
                            CliIdentity.programName + " " + OperationCatalog.synopsis entry
                        ]
                ]
            | None ->
                errors
                |> List.collect (function
                    | UsageError.UnknownCommand(_, suggestions) -> suggestions
                    | _ -> [])
                |> List.distinct
                |> List.map (fun suggestion ->
                    message
                        language
                        "suggestion"
                        [ "suggestion", CliIdentity.programName + " " + suggestion ])

        String.concat "\n" ((errors |> List.map (line language)) @ hints) + "\n"
