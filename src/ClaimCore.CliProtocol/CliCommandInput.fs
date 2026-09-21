namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Domain

/// Strict CLI-v3 command decoder. It creates only adapter input; the single Application binder
/// remains responsible for constructing a closed Domain request.
module internal CliCommandInput =
    let private failure code message path =
        Error(ProtocolFailure.create code message path)

    let private requiredOption code message path =
        function
        | Some item -> Ok item
        | None -> failure code message path

    let private canonicalGuid path (value: JsonElement) =
        match StrictJson.stringAt path value with
        | Error problem -> Error problem
        | Ok raw ->
            match Guid.TryParseExact(raw, "D") with
            | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = raw -> Ok parsed
            | _ -> failure "INVALID_UUID" "Use a non-empty canonical lowercase UUID." path

    let private revision path (value: JsonElement) =
        match StrictJson.stringAt path value with
        | Error problem -> Error problem
        | Ok raw when raw = "0" -> Ok 0L
        | Ok raw when raw.Length > 0 && raw[0] <> '0' && raw |> Seq.forall Char.IsAsciiDigit ->
            match Int64.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, parsed when parsed < Int64.MaxValue -> Ok parsed
            | _ ->
                failure
                    "INVALID_REVISION"
                    "Use a canonical unsigned revision below Int64.MaxValue."
                    path
        | Ok _ ->
            failure
                "INVALID_REVISION"
                "Use a canonical unsigned revision below Int64.MaxValue."
                path

    let private requiredString path name input =
        StrictJson.requiredProperty path name input
        |> Result.bind (StrictJson.stringAt (path + "/" + name))

    let private commandKind path (value: JsonElement) =
        StrictJson.stringAt path value
        |> Result.bind (fun token ->
            CommandKinds.all
            |> List.tryFind (fun kind -> CommandKinds.token kind = token)
            |> requiredOption
                "INVALID_COMMAND"
                "The command kind is not declared by the semantic contract."
                path)

    let private values path (expected: FieldInputDefinition list) (value: JsonElement) =
        let names = expected |> List.map _.FieldName

        StrictJson.exactProperties path names value
        |> Result.bind (fun source ->
            names
            |> List.map (fun name ->
                StrictJson.requiredProperty path name source
                |> Result.bind (StrictJson.stringAt (path + "/" + name))
                |> Result.map (fun text -> name, text))
            |> List.fold
                (fun state item ->
                    match state, item with
                    | Ok collected, Ok decoded -> Ok(decoded :: collected)
                    | Error problem, _ -> Error problem
                    | _, Error problem -> Error problem)
                (Ok [])
            |> Result.map List.rev)

    let private correctionAction (group: CorrectionGroupDefinition) (value: JsonElement) =
        let path = "/input/command/groups/" + group.Name

        StrictJson.requiredProperty path "mode" value
        |> Result.bind (StrictJson.stringAt (path + "/mode"))
        |> Result.bind (fun mode ->
            let allowed =
                group.Actions
                |> List.map (function
                    | CorrectionGroupAction.Keep -> "KEEP"
                    | CorrectionGroupAction.Replace -> "REPLACE"
                    | CorrectionGroupAction.Clear -> "CLEAR")

            if not (List.contains mode allowed) then
                failure
                    "INVALID_COMMAND"
                    "The correction action is not declared by the semantic contract."
                    path
            else
                match mode with
                | "KEEP" ->
                    StrictJson.exactProperties path [ "mode" ] value
                    |> Result.map (fun _ -> CorrectionDraftAction.Keep)
                | "CLEAR" ->
                    StrictJson.exactProperties path [ "mode" ] value
                    |> Result.map (fun _ -> CorrectionDraftAction.Clear)
                | "REPLACE" ->
                    StrictJson.exactProperties path [ "mode"; "values" ] value
                    |> Result.bind (fun source ->
                        StrictJson.requiredProperty path "values" source
                        |> Result.bind (values (path + "/values") group.ReplaceFields)
                        |> Result.map CorrectionDraftAction.Replace)
                | _ ->
                    failure
                        "INVALID_COMMAND"
                        "The correction action is not declared by the semantic contract."
                        path)

    let private correctionGroups (value: JsonElement) =
        match (CommandDefinitions.forKind CommandKind.CorrectCase).Inputs with
        | CommandInputShape.Fields _ ->
            invalidOp "The correction command must declare tagged groups."
        | CommandInputShape.CorrectionGroups definitions ->
            let expected = definitions |> List.map _.Name

            StrictJson.exactProperties "/input/command/groups" expected value
            |> Result.bind (fun source ->
                let action name =
                    let definition = definitions |> List.find (fun item -> item.Name = name)

                    StrictJson.requiredProperty "/input/command/groups" name source
                    |> Result.bind (correctionAction definition)

                match action "registration", action "decision", action "payment" with
                | Ok registration, Ok decision, Ok payment ->
                    Ok(DraftCommand.Correction(registration, decision, payment))
                | Error problem, _, _
                | _, Error problem, _
                | _, _, Error problem -> Error problem)

    let draft (input: JsonElement) =
        let command source =
            StrictJson.requiredProperty "/input" "command" source
            |> Result.bind (fun value ->
                StrictJson.objectAt "/input/command" value
                |> Result.bind (StrictJson.requiredProperty "/input/command" "kind")
                |> Result.bind (commandKind "/input/command/kind")
                |> Result.bind (fun kind ->
                    match (CommandDefinitions.forKind kind).Inputs with
                    | CommandInputShape.Fields fields ->
                        StrictJson.exactProperties "/input/command" [ "kind"; "values" ] value
                        |> Result.bind (fun source ->
                            StrictJson.requiredProperty "/input/command" "values" source
                            |> Result.bind (values "/input/command/values" fields)
                            |> Result.map (fun supplied -> DraftCommand.Flat(kind, supplied)))
                    | CommandInputShape.CorrectionGroups _ ->
                        StrictJson.exactProperties "/input/command" [ "kind"; "groups" ] value
                        |> Result.bind (fun source ->
                            StrictJson.requiredProperty "/input/command" "groups" source
                            |> Result.bind correctionGroups)))

        StrictJson.exactProperties
            "/input"
            [ "operationId"; "caseReference"; "expectedRevision"; "command" ]
            input
        |> Result.bind (fun source ->
            StrictJson.requiredProperty "/input" "operationId" source
            |> Result.bind (canonicalGuid "/input/operationId")
            |> Result.bind (fun operationId ->
                requiredString "/input" "caseReference" source
                |> Result.bind (fun caseReference ->
                    StrictJson.requiredProperty "/input" "expectedRevision" source
                    |> Result.bind (revision "/input/expectedRevision")
                    |> Result.bind (fun expectedVersion ->
                        command source
                        |> Result.map (fun commandValue ->
                            EndpointInput.Draft
                                {
                                    OperationId = operationId
                                    CaseReference = caseReference
                                    ExpectedVersion = expectedVersion
                                    Command = commandValue
                                })))))
