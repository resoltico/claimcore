namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module internal DiagnosticText =
    open ReplyTextFormat

    let diagnosticTargetIdentity target =
        ContractDiscovery.inputLabel (InteractionInput.Target target)

    let reference request =
        let target input =
            match input with
            | OperationInput.Case request -> Some request.Target
            | OperationInput.Work request -> Some request.Target
            | OperationInput.Reminder request -> Some request.Target
            | OperationInput.Authority _ -> None

        request
        |> Option.bind (fun frame ->
            [
                Frames.value Api.operationExecute frame |> Option.bind target
                Frames.value Api.operationPreview frame |> Option.bind target
                Frames.value Api.caseGet frame |> Option.map _.Target
                Frames.value Api.caseTimeline frame |> Option.map _.Target
                Frames.value Api.reminder frame |> Option.map _.Target
                Frames.value Api.reminders frame |> Option.bind _.Target
                Frames.value Api.caseAssignees frame |> Option.map _.Target
            ]
            |> List.tryPick id)
        |> Option.bind (function
            | CaseSelector.Reference reference -> Some reference
            | CaseSelector.Id _ -> None)

    let private requiredRoles language required =
        Authority.rolesFor required
        |> List.map (term language Role.vocabulary)
        |> List.reduce (fun first second ->
            MessageCatalogs.render
                language
                "notice.alternativeRoles"
                (Map.ofList [ "first", first; "second", second ]))

    let private caseCommand verb reference =
        CliIdentity.programName
        + " case "
        + verb
        + " "
        + CommandGrammar.quoteArgument reference

    let private next language reference required action =
        let message =
            match action with
            | RecommendedAction.CorrectInput -> Some CliMessage.NextCorrectInput
            | RecommendedAction.ReadCurrent -> Some CliMessage.NextReadCurrent
            | RecommendedAction.RetrySafe -> Some CliMessage.NextRetrySafe
            | RecommendedAction.StopAndInvestigate -> Some CliMessage.NextInvestigate
            | RecommendedAction.ChooseAnotherReference -> Some CliMessage.NextChooseReference
            | RecommendedAction.AskForAccess ->
                Some(
                    required
                    |> Option.map (requiredRoles language >> CliMessage.NextAskRoles)
                    |> Option.defaultValue CliMessage.NextAskAccess
                )
            | RecommendedAction.ReviewLatest ->
                Some(
                    reference
                    |> Option.map (
                        caseCommand "get" >> TerminalText.data >> CliMessage.NextReviewCommand
                    )
                    |> Option.defaultValue CliMessage.NextReviewLatest
                )
            | RecommendedAction.NoneRequired -> None

        message
        |> Option.toList
        |> List.map (fun message ->
            line
                language
                (CliMessages.key CliMessage.NextLabel)
                (MessageCatalogs.renderCli language message))

    let private conflictEntry language (entry: CaseTimelineEntry) =
        let name = actorName language entry.RecordedBy |> Some |> displayed language
        let heading = name + " · " + instant entry.RecordedAt

        let changes =
            match entry.Changes with
            | TimelineChanges.Withheld -> [ label language "label.changesWithheld" ]
            | TimelineChanges.Erased -> [ label language "label.changesErased" ]
            | TimelineChanges.Disclosed disclosure ->
                disclosure.Items
                |> List.collect TimelineCsv.scalarDifferences
                |> List.map (fun (key, before, after) ->
                    let field =
                        Vocabulary.tryParse BusinessField.vocabulary key
                        |> Option.map (FieldPresentation.labelId >> label language)
                        |> Option.defaultValue (TerminalText.data key)

                    field + ": " + TerminalText.data before + " → " + TerminalText.data after)

        heading :: changes

    let private conflictLines language reference =
        function
        | Refusal.RevisionConflict conflict ->
            match conflict.Changes with
            | Some ConflictChanges.Withheld -> [ label language "label.changesWithheld" ]
            | Some(ConflictChanges.Page page) ->
                (page.Entries |> List.collect (conflictEntry language))
                @ (match reference, page.NextCursor with
                   | Some reference, Some cursor ->
                       [
                           MessageCatalogs.renderCli
                               language
                               (CliMessage.ConflictMore(
                                   TerminalText.data (
                                       caseCommand "timeline" reference
                                       + " --cursor "
                                       + CommandGrammar.quoteArgument cursor
                                   )
                               ))
                       ]
                   | None, Some cursor ->
                       [
                           MessageCatalogs.renderCli
                               language
                               (CliMessage.PageMore(TerminalText.data cursor))
                       ]
                   | _, None -> [])
            | None -> []
        | _ -> []

    let refusalLinesFor language reference refusal =
        let render code parameters =
            let arguments =
                parameters
                |> List.map (fun (name, value) ->
                    name,
                    match value with
                    | JsonValue.Integer value -> number value
                    | JsonValue.String value -> value
                    | _ -> invalidOp "Diagnostic arguments are declared scalars.")
                |> Map.ofList

            MessageCatalogs.render language (DiagnosticCode.key code) arguments

        let messages =
            match refusal with
            | Refusal.InvalidInput issues ->
                InputIssues.toList issues
                |> List.map (fun issue ->
                    let code, parameters = DiagnosticCodecs.describeIssue issue

                    label language (diagnosticTargetIdentity issue.Target)
                    + ": "
                    + render code parameters
                    + (match issue.RelatedDate with
                       | None -> ""
                       | Some related ->
                           " ("
                           + label language (FieldPresentation.labelId related.Field)
                           + ": "
                           + CalendarDate.format related.Value
                           + ")"))
            | Refusal.Access(AccessRefusal.NotAuthorized required) ->
                let names = requiredRoles language required

                [
                    MessageCatalogs.render
                        language
                        (DiagnosticCode.key "NOT_AUTHORIZED")
                        (Map.ofList [ "required", names ])
                ]
            | Refusal.Case(CaseRefusal.NotOffered state) ->
                [
                    MessageCatalogs.render
                        language
                        (DiagnosticCode.key "NOT_OFFERED")
                        (Map.ofList [ "state", term language CaseHeadline.vocabulary state ])
                ]
            | Refusal.Reminder(ReminderRuleRefusal.NotOffered state) ->
                [
                    MessageCatalogs.render
                        language
                        (DiagnosticCode.key "REMINDER_NOT_OFFERED")
                        (Map.ofList [ "state", term language ReminderState.vocabulary state ])
                ]
            | refusal ->
                let code, parameters = DiagnosticCodecs.describe refusal
                let message = render code parameters

                match refusal with
                | Refusal.InvalidCursor ->
                    [
                        label language (diagnosticTargetIdentity InputTarget.Cursor)
                        + ": "
                        + message
                    ]
                | _ -> [ message ]

        messages
        @ conflictLines language reference refusal
        @ [
            line language (CliMessages.key CliMessage.CodeLabel) (DiagnosticCodecs.code refusal)
        ]
        @ next
            language
            reference
            (match refusal with
             | Refusal.Access(AccessRefusal.NotAuthorized required) -> Some required
             | _ -> None)
            refusal.Action

    let refusalLines language refusal = refusalLinesFor language None refusal

    let faultLines language fault =
        let code = CoreFault.token fault

        [
            label language (DiagnosticCode.key code)
            line language (CliMessages.key CliMessage.CodeLabel) code
        ]
        @ next language None None fault.Action

    let readLinesFor language reference success outcome =
        Outcome.fold success (refusalLinesFor language reference) (faultLines language) outcome

    let readLines language success outcome =
        Outcome.fold success (refusalLines language) (faultLines language) outcome
