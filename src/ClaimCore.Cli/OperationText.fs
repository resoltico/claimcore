namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module internal OperationText =
    open ClaimCore.Presentation

    open ReplyTextFormat
    open DiagnosticText

    let receiptLinesWithDetails
        details
        language
        alreadyRecorded
        (receipt: Presented<OperationReceipt>)
        =
        [
            label
                language
                (if alreadyRecorded then
                     "label.alreadyRecorded"
                 else
                     "label.recorded")
        ]
        @ ViewText.renderSelected details language receipt.View

    let receiptLines language alreadyRecorded receipt =
        receiptLinesWithDetails false language alreadyRecorded receipt

    let requestInput request =
        request
        |> Option.bind (fun invocation ->
            Frames.value Api.operationExecute invocation
            |> Option.orElseWith (fun () -> Frames.value Api.operationPreview invocation))

    let private authorityTarget language input (review: AuthorityReview) =
        match review.Target, review.After.Profile with
        | Some target, _ -> displayed language (Some(actorName language target.Actor))
        | None, Some profile -> displayed language (Some(PartyName.format profile.DisplayName))
        | None, None ->
            match input with
            | Some(OperationInput.Authority value) ->
                String.concat
                    " "
                    [
                        principalKind language value.Target.Kind
                        TerminalText.data value.Target.Issuer
                        displayed language (Some value.Target.Subject)
                    ]
            | _ -> "—"

    let private reviewIdentity language input target =
        let act =
            input
            |> Option.map (function
                | OperationInput.Case value ->
                    term language CaseActKind.vocabulary (CaseAct.kind value.Act)
                | OperationInput.Note value ->
                    term language NoteActKind.vocabulary (AuthoredNoteAct.kind value.Act)
                | OperationInput.Work _ ->
                    term language WorkActKind.vocabulary WorkActKind.UpdateWork
                | OperationInput.Reminder value ->
                    term language ReminderActKind.vocabulary (AuthoredReminderAct.kind value.Act)
                | OperationInput.Authority value ->
                    term language AuthorityKind.vocabulary (AuthorityAct.kind value.Act))
            |> Option.defaultValue "—"

        act + " " + target

    let private presentedTarget language input =
        function
        | PresentedReviewSubject.Case value ->
            TerminalText.data (CaseReference.format value.Proposed.Reference)
        | PresentedReviewSubject.Note value ->
            TerminalText.data (CaseReference.format value.Reference)
        | PresentedReviewSubject.Work value ->
            TerminalText.data (CaseReference.format value.Reference)
        | PresentedReviewSubject.Reminder value ->
            TerminalText.data (CaseReference.format value.Reference)
        | PresentedReviewSubject.Authority value -> authorityTarget language input value

    let private reviewedVersion =
        function
        | Some(OperationInput.Case request) ->
            Some(InputTarget.ExpectedRevision, request.ExpectedRevision)
        | Some(OperationInput.Work request) ->
            Some(InputTarget.ExpectedWorkRevision, request.ExpectedRevision)
        | Some(OperationInput.Reminder request) ->
            match request.Act with
            | ReminderAct.Create _ -> None
            | ReminderAct.Update(_, expected, _)
            | ReminderAct.Complete(_, expected)
            | ReminderAct.Cancel(_, expected)
            | ReminderAct.Reopen(_, expected, _)
            | ReminderAct.CompleteAndPlanNext(_, expected, _) ->
                Some(InputTarget.ExpectedReminderRevision, expected)
        | _ -> None

    let private presentedReviewLines details language request (review: PresentedReview) =
        let input = requestInput request

        let basis =
            match review.Subject with
            | PresentedReviewSubject.Case value -> value.Context.Basis
            | PresentedReviewSubject.Note value -> value.Context.Basis
            | PresentedReviewSubject.Work value -> value.Context.Basis
            | PresentedReviewSubject.Reminder value -> value.Context.Basis
            | PresentedReviewSubject.Authority value -> value.Basis

        [
            line
                language
                (CliMessages.key CliMessage.ReviewTitle)
                (reviewIdentity language input (presentedTarget language input review.Subject))
        ]
        @ (review.Presentation.Changes
           |> List.filter (fun change ->
               match change.Before, change.After with
               | PresentedValue.Identifier _, _
               | _, PresentedValue.Identifier _ -> details
               | _ -> true)
           |> List.map (fun change ->
               line
                   language
                   change.LabelId
                   (TextDescriptorValue.render language change.Before
                    + " → "
                    + TextDescriptorValue.render language change.After)))
        @ [
            line
                language
                "label.unchanged"
                (if review.Presentation.Unchanged.IsEmpty then
                     "—"
                 else
                     review.Presentation.Unchanged
                     |> List.map (fun entry -> label language entry.LabelId)
                     |> List.distinct
                     |> String.concat ", ")
        ]
        @ ViewText.renderSelected details language review.View
        @ (if details then
               reviewedVersion input
               |> Option.map (fun (target, revision) ->
                   line
                       language
                       (TaskInputPresentation.messageKey (TaskInputId.Target target) "label")
                       (revision.ToString(CultureInfo.InvariantCulture)))
               |> Option.toList
           else
               [])
        @ [ TextObservation.line language basis ]

    let previewLinesWithDetails details language request (reply: PresentedPreviewReply) =
        readLinesFor
            language
            (DiagnosticText.reference request)
            (function
            | PresentedPreviewed.Reviewed review ->
                presentedReviewLines details language request review
            | PresentedPreviewed.AlreadyAccepted receipt ->
                receiptLinesWithDetails details language true receipt)
            reply.Outcome

    let previewLines language request reply =
        previewLinesWithDetails false language request reply

    let executeLinesForWithDetails details language request (reply: PresentedExecuteReply) =
        let failed outcome =
            readLinesFor language (DiagnosticText.reference request) (fun () -> []) outcome

        match reply.Outcome with
        | PresentedExecuteResult.Accepted receipt ->
            receiptLinesWithDetails details language false receipt
        | PresentedExecuteResult.AlreadyAccepted receipt ->
            receiptLinesWithDetails details language true receipt
        | PresentedExecuteResult.Rejected refusal -> failed (Outcome.Rejected refusal)
        | PresentedExecuteResult.Failed fault -> failed (Outcome.Failed fault)
        | PresentedExecuteResult.OutcomeUnknown ->
            [ label language (CliMessages.key CliMessage.UncertainDelivery) ]

    let executeLinesFor language request reply =
        executeLinesForWithDetails false language request reply

    let executeLines language reply = executeLinesFor language None reply

    let getLinesWithDetails details language (reply: Outcome<PresentedLookupResult>) =
        readLines
            language
            (function
            | PresentedLookupResult.Accepted(receipt, _) ->
                receiptLinesWithDetails details language false receipt
            | PresentedLookupResult.NotAccepted view ->
                ViewText.renderSelected details language view
            | PresentedLookupResult.Pending(_, view) ->
                ViewText.renderSelected details language view)

            reply

    let getLines language reply =
        getLinesWithDetails false language reply
