namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

module HumanReviewDocument =
    let private describe task operationId binding seeds (review: OperationReview) =
        let confirmation =
            match review.Target with
            | ReviewTarget.Case(_, reference) -> ConfirmationTarget.CaseReference reference
            | ReviewTarget.Person _ -> ConfirmationTarget.Operation operationId

        let screen, presentation = ReviewDocumentParts.describe (Some operationId) review
        let action = TaskAffordances.offered task (Some binding) seeds [] Map.empty

        {
            HumanReview.Document =
                HumanDocument.create
                    (DocumentSubject.Review(confirmation, TaskConsequences.ofTask task))
                    screen
                    [ action ]
                    []
                    None
                    (ObservationBasis.Snapshot review.Context.Basis)
                    HumanDisclosure.Current
                    None
            Presentation = presentation
        }

    let ofSubmission (submission: OperationSubmission) review =
        describe
            submission.Task
            submission.OperationId
            review.Binding
            submission.Envelope.Inputs
            review

module HumanReceiptDocument =
    let ofCore (receipt: OperationReceipt) =
        let parent, disclosure, follow =
            match receipt.Subject with
            | ReceiptSubject.Case value ->
                let disclosure =
                    match value.Snapshot with
                    | SnapshotDisclosure.Disclosed _ -> HumanDisclosure.Current
                    | SnapshotDisclosure.Withheld -> HumanDisclosure.Withheld
                    | SnapshotDisclosure.Erased -> HumanDisclosure.Erased

                Some value.CaseId,
                disclosure,
                (match value.Snapshot with
                 | SnapshotDisclosure.Disclosed { State = LiveCase.Active fields } ->
                     fields.Status = CaseStatus.Opened
                 | _ -> false)
            | ReceiptSubject.Work value ->
                let disclosure =
                    match value.Snapshot with
                    | WorkDisclosure.Disclosed _ -> HumanDisclosure.Current
                    | WorkDisclosure.Withheld -> HumanDisclosure.Withheld
                    | WorkDisclosure.Erased -> HumanDisclosure.Erased

                Some value.CaseId, disclosure, true
            | ReceiptSubject.Note value ->
                let disclosure =
                    match value.Snapshot with
                    | NoteDisclosure.Disclosed _ -> HumanDisclosure.Current
                    | NoteDisclosure.Withheld -> HumanDisclosure.Withheld
                    | NoteDisclosure.Erased -> HumanDisclosure.Erased

                Some value.CaseId, disclosure, false
            | ReceiptSubject.Reminder value ->
                let versions =
                    match value.Effects with
                    | ReminderReceiptEffects.Single value -> [ value ]
                    | ReminderReceiptEffects.CompletedAndPlanned(a, b) -> [ a; b ]

                let disclosures =
                    versions
                    |> List.map (fun value ->
                        match value.Snapshot with
                        | ReminderDisclosure.Disclosed _ -> HumanDisclosure.Current
                        | ReminderDisclosure.Withheld -> HumanDisclosure.Withheld
                        | ReminderDisclosure.Erased -> HumanDisclosure.Erased)
                    |> List.distinct

                Some value.CaseId,
                (match disclosures with
                 | [ value ] -> value
                 | _ -> HumanDisclosure.Partial),
                true
            | ReceiptSubject.People value ->
                None,
                (match value.TargetActor with
                 | PeopleTargetDisclosure.Disclosed _ -> HumanDisclosure.Current
                 | PeopleTargetDisclosure.Withheld _ -> HumanDisclosure.Withheld),
                false

        let links =
            parent
            |> Option.map (fun id ->
                [
                    {
                        NamedLink.Relation = LinkRelation.OpenCase
                        LabelId = "field.caseReference.label"
                        Link = Link.caseById id
                    }
                    if follow then
                        {
                            NamedLink.Relation = LinkRelation.FollowUp
                            LabelId = "field.caseReference.label"
                            Link = Link.caseById id
                        }
                ])
            |> Option.defaultValue []

        HumanDocument.create
            (DocumentSubject.Receipt receipt.OperationId)
            (ReceiptScreen.view receipt)
            []
            links
            None
            (ObservationBasis.Snapshot receipt.Basis)
            disclosure
            None

module HumanOperationReply =
    let classifyPreview (reply: HumanOperationReply<HumanPreviewResult>) =
        match reply.Outcome with
        | HumanPreviewResult.Reviewed _ -> ReplyKind.Reviewed
        | HumanPreviewResult.AlreadyAccepted _ -> ReplyKind.AlreadyAccepted
        | HumanPreviewResult.Rejected _ -> ReplyKind.Rejected
        | HumanPreviewResult.Failed _ -> ReplyKind.Failed

    let classifyExecute (reply: HumanOperationReply<HumanExecuteResult>) =
        match reply.Outcome with
        | HumanExecuteResult.Accepted _ -> ReplyKind.Accepted
        | HumanExecuteResult.AlreadyAccepted _ -> ReplyKind.AlreadyAccepted
        | HumanExecuteResult.Rejected _ -> ReplyKind.Rejected
        | HumanExecuteResult.Failed _ -> ReplyKind.Failed
        | HumanExecuteResult.OutcomeUnknown -> ReplyKind.OutcomeUnknown

    let classifyLookup (reply: HumanOperationReply<HumanLookupResult>) =
        match reply.Outcome with
        | HumanLookupResult.Accepted _ -> ReplyKind.Accepted
        | HumanLookupResult.Pending _ -> ReplyKind.Pending
        | HumanLookupResult.NotAccepted _ -> ReplyKind.NotAccepted
        | HumanLookupResult.Rejected _ -> ReplyKind.Rejected
        | HumanLookupResult.Failed _ -> ReplyKind.Failed
