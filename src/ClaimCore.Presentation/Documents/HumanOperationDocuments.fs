namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

module HumanReviewDocument =
    let private describe act operationId binding seeds authored review =
        let basis, reference =
            match review with
            | OperationReview.Case value -> value.Context.Basis, Some value.Proposed.Reference
            | OperationReview.Work value -> value.Context.Basis, Some value.Reference
            | OperationReview.Note value -> value.Context.Basis, Some value.Reference
            | OperationReview.Reminder value -> value.Context.Basis, Some value.Reference
            | OperationReview.People value -> value.Basis, None

        let confirmation =
            reference
            |> Option.map ConfirmationTarget.CaseReference
            |> Option.defaultValue (ConfirmationTarget.Operation operationId)

        let screen, presentation =
            ReviewDocumentParts.describe (Some(act, operationId)) authored review

        let action =
            TaskAffordances.offered (TaskId.Operation act) binding seeds [] Map.empty

        {
            HumanReview.Document =
                HumanDocument.create
                    (DocumentSubject.Review(confirmation, Consequence.ofAct act))
                    screen
                    [ { action with Primary = true } ]
                    []
                    None
                    (ObservationBasis.Snapshot basis)
                    HumanDisclosure.Current
                    None
            Presentation = presentation
        }

    let ofSubmission (submission: OperationSubmission) review =
        let authored =
            match submission.Operation with
            | Ok(SubmittedOperation.Command input) -> Some input
            | _ -> None

        describe
            submission.Task
            submission.OperationId
            submission.Binding
            submission.Inputs
            authored
            review

    /// Typed Core callers retain their command structure without serializing a protocol request.
    let ofCore input review =
        let binding =
            match input with
            | OperationInput.Case value ->
                let payment =
                    match value.Act with
                    | CaseAct.RemovePayment id -> Some id
                    | _ -> None

                OperationBinding.Case(
                    value.Target,
                    value.ExpectedRevision,
                    payment,
                    value.ExpectedFamilySequence
                )
            | OperationInput.Work value ->
                OperationBinding.Work(value.Target, value.ExpectedRevision)
            | OperationInput.Note value ->
                let selected =
                    match value.Act with
                    | AuthoredNoteAct.RemoveNote id -> Some id
                    | _ -> None

                OperationBinding.Note(value.Target, selected)
            | OperationInput.Reminder value ->
                let current =
                    match value.Act with
                    | ReminderAct.Create _ -> None
                    | ReminderAct.Update(id, revision, _)
                    | ReminderAct.CompleteAndPlanNext(id, revision, _)
                    | ReminderAct.Complete(id, revision)
                    | ReminderAct.Cancel(id, revision)
                    | ReminderAct.Reopen(id, revision, _) -> Some(id, revision)

                OperationBinding.Reminder(value.Target, current)
            | OperationInput.People request ->
                let actor =
                    match review with
                    | OperationReview.People value ->
                        value.Target |> Option.map (fun value -> ActorId.value value.Actor.ActorId)
                    | _ -> None

                OperationBinding.People(actor, request.ExpectedRevision)

        describe
            (ActKind.ofInput input)
            (OperationInput.operationId input)
            binding
            Map.empty
            (Some input)
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
                List.contains
                    value.Act
                    [ CaseActKind.Open; CaseActKind.Reopen; CaseActKind.Reinstate ]
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
