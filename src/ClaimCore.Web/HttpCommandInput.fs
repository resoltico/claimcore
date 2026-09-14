namespace ClaimCore.Web

open System.Text.Json
open ClaimCore.Application
open ClaimCore.Domain
open HttpInputSupport

module HttpCommandInput =
    let private commandKind value =
        CommandKinds.all
        |> List.tryFind (fun kind -> CommandKinds.token kind = value)
        |> Option.defaultWith (fun () -> fail "The command token is not supported.")

    let private commandValues (expected: FieldInputDefinition list) values =
        let names = expected |> List.map _.FieldName
        exactProperties names values |> ignore

        if names |> List.exists (fun name -> not (values.ContainsKey name)) then
            fail "A required command value is missing."

        names |> List.map (fun name -> name, values[name] |> stringValue)

    let private correctionAction (group: CorrectionGroupDefinition) (value: JsonElement) =
        let values = properties value
        let mode = required "mode" values |> stringValue

        let allowed =
            group.Actions
            |> List.map (function
                | CorrectionGroupAction.Keep -> "KEEP"
                | CorrectionGroupAction.Replace -> "REPLACE"
                | CorrectionGroupAction.Clear -> "CLEAR")

        if not (List.contains mode allowed) then
            fail "The correction action is not declared by the semantic contract."

        match mode with
        | "KEEP" ->
            exactProperties [ "mode" ] values |> ignore
            CorrectionDraftAction.Keep
        | "CLEAR" ->
            exactProperties [ "mode" ] values |> ignore
            CorrectionDraftAction.Clear
        | "REPLACE" ->
            exactProperties [ "mode"; "values" ] values |> ignore
            let replacement = required "values" values |> properties
            commandValues group.ReplaceFields replacement |> CorrectionDraftAction.Replace
        | _ -> fail "The correction action is not declared by the semantic contract."

    let private correctionCommand (groups: Map<string, JsonElement>) =
        match (CommandDefinitions.forKind CommandKind.CorrectCase).Inputs with
        | CommandInputShape.Fields _ ->
            invalidOp "The correction command must declare its tagged input groups."
        | CommandInputShape.CorrectionGroups definitions ->
            let names = definitions |> List.map _.Name
            exactProperties names groups |> ignore

            let read name =
                let definition = definitions |> List.find (fun item -> item.Name = name)
                required name groups |> correctionAction definition

            DraftCommand.Correction(read "registration", read "decision", read "payment")

    let draft (root: JsonElement) : CommandDraft =
        let values =
            properties root
            |> exactProperties [ "operationId"; "caseReference"; "expectedRevision"; "command" ]

        let commandBody = required "command" values |> properties
        let kind = required "kind" commandBody |> stringValue |> commandKind

        let command =
            match (CommandDefinitions.forKind kind).Inputs with
            | CommandInputShape.Fields fields ->
                exactProperties [ "kind"; "values" ] commandBody |> ignore

                required "values" commandBody
                |> properties
                |> commandValues fields
                |> fun supplied -> DraftCommand.Flat(kind, supplied)
            | CommandInputShape.CorrectionGroups _ ->
                exactProperties [ "kind"; "groups" ] commandBody |> ignore

                required "groups" commandBody |> properties |> correctionCommand

        {
            OperationId = required "operationId" values |> stringValue |> operationIdValue
            CaseReference = required "caseReference" values |> stringValue
            ExpectedVersion =
                required "expectedRevision" values |> stringValue |> canonicalNonNegativeInt64
            Command = command
        }
