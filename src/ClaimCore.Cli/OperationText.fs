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

    let receiptLines language alreadyRecorded (receipt: Presented<OperationReceipt>) =
        [
            label
                language
                (if alreadyRecorded then
                     "label.alreadyRecorded"
                 else
                     "label.recorded")
        ]
        @ ViewText.render language receipt.View
        @ [ TextObservation.line language receipt.Data.Basis ]

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
        | PresentedReviewSubject.Work value ->
            TerminalText.data (CaseReference.format value.Reference)
        | PresentedReviewSubject.Reminder value ->
            TerminalText.data (CaseReference.format value.Reference)
        | PresentedReviewSubject.Authority value -> authorityTarget language input value

    let private presentedReviewLines language request (review: PresentedReview) =
        let input = requestInput request

        let basis =
            match review.Subject with
            | PresentedReviewSubject.Case value -> value.Context.Basis
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
        @ ViewText.render language review.View
        @ [ TextObservation.line language basis ]

    let previewLines language request (reply: PresentedPreviewReply) =
        readLinesFor
            language
            (DiagnosticText.reference request)
            (function
            | PresentedPreviewed.Reviewed review -> presentedReviewLines language request review
            | PresentedPreviewed.AlreadyAccepted receipt -> receiptLines language true receipt)
            reply.Outcome

    let executeLinesFor language request (reply: PresentedExecuteReply) =
        let failed outcome =
            readLinesFor language (DiagnosticText.reference request) (fun () -> []) outcome

        match reply.Outcome with
        | PresentedExecuteResult.Accepted receipt -> receiptLines language false receipt
        | PresentedExecuteResult.AlreadyAccepted receipt -> receiptLines language true receipt
        | PresentedExecuteResult.Rejected refusal -> failed (Outcome.Rejected refusal)
        | PresentedExecuteResult.Failed fault -> failed (Outcome.Failed fault)
        | PresentedExecuteResult.OutcomeUnknown ->
            [ label language (CliMessages.key CliMessage.UncertainDelivery) ]

    let executeLines language reply = executeLinesFor language None reply

    let getLines language (reply: Outcome<PresentedLookupResult>) =
        readLines
            language
            (function
            | PresentedLookupResult.Accepted(receipt, _) -> receiptLines language false receipt
            | PresentedLookupResult.NotAccepted -> [ label language "notice.notAccepted" ]
            | PresentedLookupResult.Pending finalAt ->
                [
                    MessageCatalogs.renderCli
                        language
                        (CliMessage.OperationPending(
                            finalAt.ToString("O", CultureInfo.InvariantCulture)
                        ))
                ])
            reply
