namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// A review exposes authored presentation and the control bindings for that exact command.
/// These handles are advisory; execute still performs current-state admission.
module HumanReviewDocument =
    let private binding =
        function
        | OperationReview.Case value ->
            ReviewTarget.Case(value.Proposed.CaseId, value.Before |> Option.map _.Revision),
            value.Context.Basis,
            Some value.Proposed.Reference
        | OperationReview.Work value ->
            ReviewTarget.Work(value.Parent.CaseId, value.Before.Revision),
            value.Context.Basis,
            Some value.Reference
        | OperationReview.Note value ->
            ReviewTarget.Note(value.Parent.CaseId, value.Before |> Option.map Note.id),
            value.Context.Basis,
            Some value.Reference
        | OperationReview.Reminder value ->
            let existing =
                value.Before
                |> Option.map (fun reminder ->
                    {
                        ReminderIdentity.ReminderId = Reminder.id reminder
                        Revision = Reminder.revision reminder
                    })

            ReviewTarget.Reminder(value.CaseId, existing), value.Context.Basis, Some value.Reference
        | OperationReview.Authority value ->
            ReviewTarget.Authority(
                value.Target |> Option.map (fun item -> item.Actor.ActorId),
                value.AccessScope
            ),
            value.Basis,
            None

    let ofCore input review =
        let target, basis, reference = binding review
        let task = TaskId.ofOperation input
        let declaration = TaskDeclarations.describe task
        let screen, presentation = ReviewDocumentParts.ofCore (Some input) review

        let context =
            {
                ReviewInteraction.Task = task
                Target = target
                Inputs =
                    (declaration.Inputs @ (declaration.Collections |> List.collect _.Inputs))
                    |> List.map _.Input
                    |> List.distinct
                Consequence = Consequence.ofAct (ActKind.ofInput input)
                Confirmation =
                    match reference with
                    | Some value -> ConfirmationTarget.CaseReference value
                    | None -> ConfirmationTarget.Operation(OperationInput.operationId input)
            }

        {
            HumanReview.Document =
                {
                    Screen = screen
                    Context = InteractionContext.Review context
                    Basis = ObservationBasis.Snapshot basis
                    Disclosure = HumanDisclosure.Current
                }
            Presentation = presentation
        }

/// Receipt handles retain identities and versions, never the disclosed snapshots they identify.
module HumanReceiptDocument =
    let private reminderIdentity (version: ReminderVersion) =
        {
            ReminderIdentity.ReminderId = version.ReminderId
            Revision = version.Revision
        }

    let private reminderDisclosure =
        function
        | ReminderDisclosure.Disclosed _ -> HumanDisclosure.Current
        | ReminderDisclosure.Withheld -> HumanDisclosure.Withheld
        | ReminderDisclosure.Erased -> HumanDisclosure.Erased

    let private targetAndDisclosure =
        function
        | ReceiptSubject.Case value ->
            ReceiptTarget.Case(value.CaseId, value.Revision),
            (match value.Snapshot with
             | SnapshotDisclosure.Disclosed _ -> HumanDisclosure.Current
             | SnapshotDisclosure.Withheld -> HumanDisclosure.Withheld
             | SnapshotDisclosure.Erased -> HumanDisclosure.Erased)
        | ReceiptSubject.Work value ->
            ReceiptTarget.Work(value.CaseId, value.Revision),
            (match value.Snapshot with
             | WorkDisclosure.Disclosed _ -> HumanDisclosure.Current
             | WorkDisclosure.Withheld -> HumanDisclosure.Withheld
             | WorkDisclosure.Erased -> HumanDisclosure.Erased)
        | ReceiptSubject.Note value ->
            ReceiptTarget.Note(value.CaseId, value.NoteId),
            (match value.Snapshot with
             | NoteDisclosure.Disclosed _ -> HumanDisclosure.Current
             | NoteDisclosure.Withheld -> HumanDisclosure.Withheld
             | NoteDisclosure.Erased -> HumanDisclosure.Erased)
        | ReceiptSubject.Reminder value ->
            let versions =
                match value.Effects with
                | ReminderReceiptEffects.Single version -> [ version ]
                | ReminderReceiptEffects.CompletedAndPlanned(completed, next) -> [ completed; next ]

            let disclosure =
                match versions |> List.map (_.Snapshot >> reminderDisclosure) |> List.distinct with
                | [ disclosure ] -> disclosure
                | _ -> HumanDisclosure.Partial

            ReceiptTarget.Reminder(value.CaseId, versions |> List.map reminderIdentity), disclosure
        | ReceiptSubject.Authority value ->
            ReceiptTarget.Authority(AuthorityTargetDisclosure.actorId value.TargetActor),
            (match value.TargetActor with
             | AuthorityTargetDisclosure.Disclosed _ -> HumanDisclosure.Current
             | AuthorityTargetDisclosure.Withheld _ -> HumanDisclosure.Withheld)

    let ofCore (receipt: OperationReceipt) =
        let target, disclosure = targetAndDisclosure receipt.Subject

        {
            HumanDocument.Screen = ReceiptScreen.view receipt
            Context =
                InteractionContext.Receipt
                    {
                        Target = target
                        RecordedFields =
                            match receipt.Subject with
                            | ReceiptSubject.Case {
                                                      Snapshot = SnapshotDisclosure.Disclosed shown
                                                  } -> shown.RecordedFields
                            | _ -> []
                        CaseLink =
                            match target with
                            | ReceiptTarget.Case(id, _)
                            | ReceiptTarget.Work(id, _)
                            | ReceiptTarget.Note(id, _)
                            | ReceiptTarget.Reminder(id, _) -> Some id
                            | ReceiptTarget.Authority _ -> None
                        RefreshCase =
                            match receipt.Subject with
                            | ReceiptSubject.Work value -> Some value.CaseId
                            | ReceiptSubject.Reminder value -> Some value.CaseId
                            | ReceiptSubject.Case value when
                                List.contains
                                    value.Act
                                    [ CaseActKind.Open; CaseActKind.Reopen; CaseActKind.Reinstate ]
                                ->
                                Some value.CaseId
                            | _ -> None
                    }
            Basis = ObservationBasis.Snapshot receipt.Basis
            Disclosure = disclosure
        }

module HumanOperationReply =
    let private classification kind exitCode = { Kind = kind; ExitCode = exitCode }

    let classifyPreview (reply: HumanOperationReply<HumanPreviewResult>) =
        match reply.Outcome with
        | HumanPreviewResult.Reviewed _ -> classification ReplyKind.Reviewed ExitCode.Success
        | HumanPreviewResult.AlreadyAccepted _ ->
            classification ReplyKind.AlreadyAccepted ExitCode.Success
        | HumanPreviewResult.Rejected _ -> classification ReplyKind.Rejected ExitCode.Rejected
        | HumanPreviewResult.Failed _ -> classification ReplyKind.Failed ExitCode.Failed

    let classifyExecute (reply: HumanOperationReply<HumanExecuteResult>) =
        match reply.Outcome with
        | HumanExecuteResult.Accepted _ -> classification ReplyKind.Accepted ExitCode.Success
        | HumanExecuteResult.AlreadyAccepted _ ->
            classification ReplyKind.AlreadyAccepted ExitCode.Success
        | HumanExecuteResult.Rejected _ -> classification ReplyKind.Rejected ExitCode.Rejected
        | HumanExecuteResult.Failed _ -> classification ReplyKind.Failed ExitCode.Failed
        | HumanExecuteResult.OutcomeUnknown ->
            classification ReplyKind.OutcomeUnknown ExitCode.OutcomeUnknown

    let classifyLookup (reply: HumanOperationReply<HumanLookupResult>) =
        match reply.Outcome with
        | HumanLookupResult.Accepted _ -> classification ReplyKind.Accepted ExitCode.Success
        | HumanLookupResult.Pending _ -> classification ReplyKind.Pending ExitCode.OutcomeUnknown
        | HumanLookupResult.NotAccepted _ -> classification ReplyKind.NotAccepted ExitCode.Rejected
        | HumanLookupResult.Rejected _ -> classification ReplyKind.Rejected ExitCode.Rejected
        | HumanLookupResult.Failed _ -> classification ReplyKind.Failed ExitCode.Failed
