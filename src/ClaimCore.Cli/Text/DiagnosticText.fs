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
        ContractDiscovery.inputLabel (TaskInputId.Target target)

    let reference request =
        let target (input: OperationSubmission) =
            match input.Envelope.Binding with
            | OperationBinding.CaseNew _ ->
                input.Envelope.Inputs
                |> Map.tryFind (TaskInputId.Target(InputTarget.Field BusinessField.CaseReference))
                |> Option.bind (function
                    | SubmissionValue.Text reference -> Some(CaseSelector.Reference reference)
                    | _ -> None)
            | OperationBinding.Case(target, _, _, _)
            | OperationBinding.Party(target, _)
            | OperationBinding.Attachment(target, _)
            | OperationBinding.Note(target, _)
            | OperationBinding.Link(target, _)
            | OperationBinding.Work(target, _)
            | OperationBinding.Reminder(target, _) -> Some target
            | OperationBinding.People _ -> None

        request
        |> Option.bind (fun frame ->
            [
                Frames.value Api.operationExecute frame |> Option.bind target
                Frames.value Api.operationPreview frame |> Option.bind target
                Frames.value Api.caseGet frame |> Option.map _.Target
                Frames.value Api.caseNotes frame |> Option.map _.Target
                Frames.value Api.caseTimeline frame |> Option.map _.Target
                Frames.value Api.reminder frame |> Option.map _.Target
                Frames.value Api.reminders frame |> Option.bind _.Target
                Frames.value Api.caseAssignees frame |> Option.map _.Target
            ]
            |> List.tryPick id)
        |> Option.bind (function
            | CaseSelector.Reference reference -> Some reference
            | CaseSelector.Id _ -> None)

    let private requiredRoles (rendering: RenderingContext) required =
        Access.rolesFor required
        |> List.map (term rendering Role.vocabulary)
        |> List.reduce (fun first second ->
            MessageCatalogs.render
                rendering.Language
                "notice.alternativeRoles"
                (Map.ofList [ "first", first; "second", second ]))

    let private caseCommand verb reference =
        CliIdentity.programName
        + " case "
        + verb
        + " "
        + CommandGrammar.quoteArgument reference

    let private next (rendering: RenderingContext) reference required action =
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
                    |> Option.map (requiredRoles rendering >> CliMessage.NextAskRoles)
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
                rendering
                (CliMessages.key CliMessage.NextLabel)
                (MessageCatalogs.renderCli rendering.Language message))

    let private conflictLines details view (rendering: RenderingContext) reference =
        function
        | Refusal.RevisionConflict conflict ->
            match conflict.Changes with
            | Some ConflictChanges.Withheld -> [ label rendering "label.changesWithheld" ]
            | Some(ConflictChanges.Page page) ->
                (view
                 |> Option.map (ViewText.renderSelectedIn page.Basis.TimeZoneId details rendering)
                 |> Option.defaultValue [])
                @ (match reference, page.NextCursor with
                   | Some reference, Some cursor ->
                       [
                           MessageCatalogs.renderCli
                               rendering.Language
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
                               rendering.Language
                               (CliMessage.PageMore(TerminalText.data cursor))
                       ]
                   | _, None -> [])
            | None -> []
        | _ -> []

    let refusalLinesWithView details view (rendering: RenderingContext) reference refusal =
        let messages =
            match refusal with
            | Refusal.InvalidInput issues ->
                InputIssues.toList issues
                |> List.map (fun issue ->
                    let code, parameters = DiagnosticCodecs.describeIssue issue

                    label rendering (diagnosticTargetIdentity issue.Target)
                    + ": "
                    + diagnostic rendering code parameters)
            | Refusal.Access(AccessRefusal.NotAuthorized required) ->
                let names = requiredRoles rendering required

                [
                    MessageCatalogs.render
                        rendering.Language
                        (DiagnosticCode.key "NOT_AUTHORIZED")
                        (Map.ofList [ "required", names ])
                ]
            | Refusal.CaseDateRange upper ->
                let key = TaskInputPresentation.messageKey (TaskInputId.CaseDate upper) "label"

                [
                    MessageCatalogs.render
                        rendering.Language
                        (DiagnosticCode.key "CASE_DATE_RANGE")
                        (Map.ofList [ "field", label rendering key ])
                ]
            | Refusal.Case(CaseRefusal.NotOffered state) ->
                [
                    MessageCatalogs.render
                        rendering.Language
                        (DiagnosticCode.key "NOT_OFFERED")
                        (Map.ofList [ "state", term rendering CaseHeadline.vocabulary state ])
                ]
            | Refusal.Reminder(ReminderRuleRefusal.NotOffered state) ->
                [
                    MessageCatalogs.render
                        rendering.Language
                        (DiagnosticCode.key "REMINDER_NOT_OFFERED")
                        (Map.ofList [ "state", term rendering ReminderState.vocabulary state ])
                ]
            | refusal ->
                let code, parameters = DiagnosticCodecs.describe refusal
                let message = diagnostic rendering code parameters

                match refusal with
                | Refusal.InvalidCursor ->
                    [
                        label rendering (diagnosticTargetIdentity InputTarget.Cursor)
                        + ": "
                        + message
                    ]
                | _ -> [ message ]

        messages
        @ conflictLines details view rendering reference refusal
        @ [
            line rendering (CliMessages.key CliMessage.CodeLabel) (DiagnosticCodecs.code refusal)
        ]
        @ next
            rendering
            reference
            (match refusal with
             | Refusal.Access(AccessRefusal.NotAuthorized required) -> Some required
             | _ -> None)
            refusal.Action

    let refusalLinesFor (rendering: RenderingContext) reference refusal =
        refusalLinesWithView false None rendering reference refusal

    let refusalLines (rendering: RenderingContext) refusal = refusalLinesFor rendering None refusal

    let faultLines (rendering: RenderingContext) fault =
        let code = CoreFault.token fault

        [
            label rendering (DiagnosticCode.key code)
            line rendering (CliMessages.key CliMessage.CodeLabel) code
        ]
        @ next rendering None None fault.Action

    let readLinesFor (rendering: RenderingContext) reference success outcome =
        Outcome.fold success (refusalLinesFor rendering reference) (faultLines rendering) outcome

    let readLines (rendering: RenderingContext) success outcome =
        Outcome.fold success (refusalLines rendering) (faultLines rendering) outcome
