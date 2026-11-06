namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Work families share ledger identity, delivery and authorization; their business decisions stay typed.
module internal WorkExecution =
    type private ReminderInput =
        | Create of ReminderPlan
        | Change of ReminderId * Revision * ReminderChange

    type Prepared =
        private
        | Work of OperationId * CaseSelector * Revision * ValidCaseWorkUpdate
        | Reminder of OperationId * CaseSelector * ReminderInput

    type private Proposal =
        {
            Subject: EntryTarget
            RequestBytes: byte array
            SnapshotBytes: byte array
            Current: ReceiptCurrent
            Projection: WorkChangeProjection
            Review: OperationReview
        }

    let private input target parse raw =
        parse raw
        |> Result.mapError (fun violation -> Refusal.invalidInput (target, violation))

    let private operation raw =
        OperationId.create raw
        |> Option.map Ok
        |> Option.defaultValue (
            Error(Refusal.invalidInput (InputTarget.OperationId, Violation.OperationIdRequired))
        )

    let private revision target minimum raw =
        Revision.create raw
        |> Option.filter (fun value -> value >= minimum)
        |> Option.map Ok
        |> Option.defaultValue (
            Error(Refusal.invalidInput (target, Violation.ExpectedRevisionRange))
        )

    let private reminderId raw =
        ReminderId.create raw
        |> Option.map Ok
        |> Option.defaultValue (
            Error(Refusal.invalidInput (InputTarget.ReminderId, Violation.ReminderIdRequired))
        )

    let private plan (raw: ReminderPlanInput) =
        Admission.map2
            (fun action due ->
                {
                    ReminderPlan.Action = action
                    Due = due
                })
            (input
                InputTarget.ReminderAction
                (ReminderAction.parse >> Result.bind ReminderAction.admit)
                raw.Action)
            (input InputTarget.ReminderDueDate CalendarDate.parse raw.Due)

    let private reminderAct =
        function
        | AuthoredReminderAct.Create value -> plan value |> Result.map Create
        | act ->
            let id, expected, change =
                match act with
                | AuthoredReminderAct.Update(id, expected, value) ->
                    id, expected, plan value |> Result.map ReminderChange.Update
                | AuthoredReminderAct.Complete(id, expected) ->
                    id, expected, Ok ReminderChange.Complete
                | AuthoredReminderAct.Cancel(id, expected) -> id, expected, Ok ReminderChange.Cancel
                | AuthoredReminderAct.Reopen(id, expected, date) ->
                    id,
                    expected,
                    input InputTarget.ReminderDueDate CalendarDate.parse date
                    |> Result.map ReminderChange.Reopen
                | AuthoredReminderAct.CompleteAndPlanNext(id, expected, value) ->
                    id, expected, plan value |> Result.map ReminderChange.CompleteAndPlanNext
                | AuthoredReminderAct.Create _ -> invalidOp "Creation is handled separately."

            Admission.map2
                (fun (id, expected) change -> Change(id, expected, change))
                (Admission.map2
                    (fun id expected -> id, expected)
                    (reminderId id)
                    (revision InputTarget.ExpectedReminderRevision Revision.first expected))
                change

    let prepareWork (request: WorkOperationRequest) =
        let (WorkAct.UpdateWork raw) = request.Act

        let update =
            CaseWorkInput.admit raw
            |> Result.mapError (fun issues ->
                issues
                |> List.map (fun (field, violation) ->
                    {
                        Target = InputTarget.Field field
                        RelatedDate = None
                        Violation = violation
                    })
                |> InputIssues.ofList
                |> Option.get
                |> Refusal.InvalidInput)

        Admission.map2
            (fun (operation, expected) update -> Work(operation, request.Target, expected, update))
            (Admission.map2
                (fun operation expected -> operation, expected)
                (operation request.OperationId)
                (revision InputTarget.ExpectedWorkRevision Revision.zero request.ExpectedRevision))
            update
        |> fun prepared ->
            Admission.map2
                (fun result () -> result)
                prepared
                (CaseExecution.validateSelector request.Target)
        |> Result.mapError (function
            | Refusal.InvalidInput issues ->
                InputIssues.toList issues
                |> List.sortBy (fun issue -> InputTarget.order issue.Target)
                |> InputIssues.ofList
                |> Option.get
                |> Refusal.InvalidInput
            | refusal -> refusal)

    let prepareReminder (request: ReminderOperationRequest) =
        Admission.map2
            (fun operation act -> Reminder(operation, request.Target, act))
            (operation request.OperationId)
            (reminderAct request.Act)
        |> fun prepared ->
            Admission.map2
                (fun result () -> result)
                prepared
                (CaseExecution.validateSelector request.Target)
        |> Result.mapError (function
            | Refusal.InvalidInput issues ->
                InputIssues.toList issues
                |> List.sortBy (fun issue -> InputTarget.order issue.Target)
                |> InputIssues.ofList
                |> Option.get
                |> Refusal.InvalidInput
            | refusal -> refusal)

    let private target =
        function
        | Work(_, target, _, _)
        | Reminder(_, target, _) -> target

    let private identity =
        function
        | Work(operation, _, _, _)
        | Reminder(operation, _, _) -> operation

    let private authorized (services: ServiceContext) (view: CaseFacts) =
        match view.Case with
        | Some parent when
            Authority.can
                services.Principal
                view.Actor
                Capability.ReadCase
                (Scope.Case parent.CaseId)
            ->
            if
                Authority.can
                    services.Principal
                    view.Actor
                    Capability.ManageCaseWork
                    (Scope.Case parent.CaseId)
            then
                Ok parent
            else
                Error(Refusal.Access(AccessRefusal.NotAuthorized Capability.ManageCaseWork))
        | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)

    let private eligibleAssignee (tx: IReadTx) caseId =
        function
        | None -> System.Threading.Tasks.Task.FromResult true
        | Some id ->
            task {
                let! actor = tx.FindActor id

                match actor with
                | Some actor when PrincipalKey.isHuman actor.Principal ->
                    let! authority =
                        tx.Authority
                            {
                                Principal = actor.Principal
                                CaseId = Some caseId
                            }

                    return
                        Authority.can
                            actor.Principal
                            authority
                            Capability.ReadCase
                            (Scope.Case caseId)
                        && Authority.can
                            actor.Principal
                            authority
                            Capability.ManageCaseWork
                            (Scope.Case caseId)
                | _ -> return false
            }

    let private workProposal
        (services: ServiceContext)
        (tx: IReadTx)
        (view: CaseFacts)
        (parent: StoredCase)
        operation
        expected
        update
        =
        task {
            let! current = tx.FindWork parent.CaseId

            let request =
                {
                    WorkRequestRecord.OperationId = operation
                    CaseId = parent.CaseId
                    ExpectedRevision = expected
                    Update = update
                }

            let bytes = WorkRecord.encodeRequest request
            let state = ReceiptCurrent.Work(Some parent, current, Map.empty)

            let! named =
                ReceiptReads.existing services tx view.Actor state view.ExistingOperation

            let replay =
                match named with
                | Error fault -> Verdict.Fail fault
                | Ok state ->
                    OperationIdentity.decide
                        services
                        view.Actor
                        {
                            OperationId = operation
                            Bytes = bytes
                            Existing = view.ExistingOperation
                        }
                        (LedgerEntries.receiptOperation
                            (ServiceContext.basis services view.ObservedAt)
                            services.Principal
                            view.Actor
                            (view.Actor.Actor |> Option.map Actor.currentReference)
                            state)

            match replay with
            | Verdict.Proceed() ->
                match current, parent.Snapshot.State with
                | _, CaseState.Erased ->
                    return Verdict.Refuse(Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Erased))
                | _, CaseState.Voided _ ->
                    return Verdict.Refuse(Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Voided))
                | Some current, CaseState.Active fields when current.Snapshot.IsSome ->
                    let before = current.Snapshot.Value

                    if view.Maintenance.Active then
                        return Verdict.Refuse Refusal.RegisterMaintenance
                    elif before.Revision <> expected then
                        return Verdict.Refuse(Refusal.revisionConflict before.Revision)
                    else
                        let next = CaseWork.apply update before.Work
                        let responsibilityChanged = next.Responsible <> before.Work.Responsible

                        let! eligible =
                            eligibleAssignee
                                tx
                                parent.CaseId
                                (match update.Responsible with
                                 | WorkValue.Set id -> Some id
                                 | _ -> None)

                        let summary =
                            match update.Summary with
                            | WorkValue.Set value when Some value <> before.Work.Summary ->
                                HandlingSummary.admit value
                                |> Result.map ignore
                                |> Result.mapError (fun violation ->
                                    Refusal.invalidInput (
                                        InputTarget.Field BusinessField.HandlingSummary,
                                        violation
                                    ))
                            | _ -> Ok()

                        if responsibilityChanged && fields.Status <> CaseStatus.Opened then
                            return
                                Verdict.Refuse(
                                    Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Closed)
                                )
                        elif not eligible then
                            return Verdict.Refuse(Refusal.Case CaseRefusal.AssigneeUnavailable)
                        elif next = before.Work then
                            return Verdict.Refuse(Refusal.Case CaseRefusal.WorkNoChanges)
                        else
                            match summary with
                            | Error refusal -> return Verdict.Refuse refusal
                            | Ok() ->
                                let after =
                                    { before with
                                        Revision = Revision.successor before.Revision
                                        Work = next
                                    }

                                let recordedBy = LedgerEnvelope.actorId view.Actor

                                let summaryChangedAt, summaryRecordedBy =
                                    if after.Work.Summary.IsNone then
                                        None, None
                                    elif before.Work.Summary = after.Work.Summary then
                                        current.SummaryChangedAt, current.SummaryRecordedBy
                                    else
                                        Some view.ObservedAt, Some recordedBy

                                let stored =
                                    { current with
                                        Revision = after.Revision
                                        Snapshot = Some after
                                        SnapshotBytes = WorkRecord.encodeSnapshot after
                                        ChangedAt = Some view.ObservedAt
                                        RecordedBy = Some recordedBy
                                        SummaryChangedAt = summaryChangedAt
                                        SummaryRecordedBy = summaryRecordedBy
                                    }

                                let relations =
                                    [ before; after ]
                                    |> List.choose (fun snapshot ->
                                        snapshot.Work.Responsible
                                        |> Option.map (fun actor -> parent.CaseId, actor))
                                    |> List.distinct

                                let! accounts = tx.AssignedAccounts relations

                                match
                                    CaseWorkReadModels.snapshot accounts before,
                                    CaseWorkReadModels.snapshot accounts after
                                with
                                | Error fault, _
                                | _, Error fault -> return Verdict.Fail fault
                                | Ok beforeRead, Ok afterRead ->
                                    return
                                        Verdict.Proceed
                                            {
                                                Subject =
                                                    EntryTarget.Work
                                                        {
                                                            CaseId = parent.CaseId
                                                            Revision = after.Revision
                                                            Act = WorkActKind.UpdateWork
                                                            BusinessDate =
                                                                services.Calendar.DateAt
                                                                    view.ObservedAt
                                                        }
                                                RequestBytes = bytes
                                                SnapshotBytes = stored.SnapshotBytes
                                                Current =
                                                    ReceiptCurrent.Work(
                                                        Some parent,
                                                        Some stored,
                                                        accounts
                                                    )
                                                Projection =
                                                    WorkChangeProjection.Work
                                                        {
                                                            Snapshot = after
                                                            ChangedAt = view.ObservedAt
                                                            RecordedBy = recordedBy
                                                            SummaryChangedAt = summaryChangedAt
                                                            SummaryRecordedBy = summaryRecordedBy
                                                        }
                                                Review =
                                                    OperationReview.Work
                                                        {
                                                            Reference = parent.Snapshot.Reference
                                                            Parent = parent.Snapshot
                                                            Before = beforeRead
                                                            Proposed = afterRead
                                                            Context =
                                                                ServiceContext.runtime
                                                                    services
                                                                    view.ObservedAt
                                                        }
                                            }
                | _ -> return Verdict.Fail CoreFault.StoreIntegrityError
            | other ->
                return
                    Verdict.map
                        (fun () -> invalidOp "Only a new operation has proposed effects.")
                        other
        }

    let private reminderRefusal (parent: StoredCase) current =
        function
        | ReminderRefusal.IdentityReserved -> Verdict.Fail CoreFault.StoreIntegrityError
        | ReminderRefusal.RevisionConflict actual -> Verdict.Refuse(Refusal.revisionConflict actual)
        | ReminderRefusal.InvalidAction violation ->
            Verdict.Refuse(Refusal.invalidInput (InputTarget.ReminderAction, violation))
        | ReminderRefusal.NotFound
        | ReminderRefusal.ParentMismatch ->
            Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
        | ReminderRefusal.ParentNotActive
        | ReminderRefusal.ParentNotOpen ->
            let state =
                parent.Snapshot.State
                |> CaseState.map (fun fields ->
                    fields.Status, CaseProgress.ofPayment fields.Progress)

            Verdict.Refuse(Refusal.Case(CaseRefusal.NotOffered(CaseHeadline.ofState state)))
        | ReminderRefusal.PendingRequired
        | ReminderRefusal.TerminalRequired ->
            match current with
            | Some reminder ->
                Verdict.Refuse(
                    Refusal.Reminder(ReminderRuleRefusal.NotOffered(Reminder.state reminder))
                )
            | None -> Verdict.Fail CoreFault.StoreIntegrityError
        | ReminderRefusal.NoChanges ->
            Verdict.Refuse(Refusal.Reminder ReminderRuleRefusal.NoChanges)

    let private reminderProposal
        (services: ServiceContext)
        (tx: IReadTx)
        (view: CaseFacts)
        (parent: StoredCase)
        operation
        act
        =
        task {
            let command =
                match act with
                | Create plan -> ReminderCommand.Create(parent.CaseId, plan)
                | Change(id, revision, change) ->
                    ReminderCommand.Change(parent.CaseId, id, revision, change)

            let request =
                {
                    ReminderRequestRecord.OperationId = operation
                    Command = command
                }

            let bytes = ReminderRecord.encodeRequest request
            let id = ReminderCommand.target operation command
            let! target = tx.FindReminder id

            let nextId =
                if ReminderCommand.kind command = ReminderActKind.CompleteAndPlanNext then
                    Some(ReminderId.ofOperationId operation)
                else
                    None

            let! next =
                match nextId with
                | Some id -> tx.FindReminder id
                | None -> System.Threading.Tasks.Task.FromResult None

            let current =
                [ target; next ]
                |> List.choose (fun value -> value)
                |> List.map (fun value -> value.ReminderId, value)
                |> Map.ofList

            let state = ReceiptCurrent.Reminder(Some parent, current)

            let replay =
                OperationIdentity.decide
                    services
                    view.Actor
                    {
                        OperationId = operation
                        Bytes = bytes
                        Existing = view.ExistingOperation
                    }
                    (LedgerEntries.receiptOperation
                        (ServiceContext.basis services view.ObservedAt)
                        services.Principal
                        view.Actor
                        (view.Actor.Actor |> Option.map Actor.currentReference)
                        state)

            match replay with
            | Verdict.Proceed() when view.Maintenance.Active ->
                return Verdict.Refuse Refusal.RegisterMaintenance
            | Verdict.Proceed() ->
                let before = target |> Option.bind _.Snapshot

                let reserved =
                    if ReminderCommand.kind command = ReminderActKind.Create then
                        target.IsSome
                    else
                        next.IsSome

                match Reminder.decide operation parent.Snapshot before reserved command with
                | Error refusal -> return reminderRefusal parent before refusal
                | Ok effects ->
                    let recordedBy = LedgerEnvelope.actorId view.Actor

                    let stored value : StoredReminder =
                        {
                            ReminderId = Reminder.id value
                            CaseId = Reminder.caseId value
                            Revision = Reminder.revision value
                            Snapshot = Some value
                            SnapshotBytes = ReminderRecord.encodeSnapshot value
                            RecordedAt = view.ObservedAt
                            RecordedBy = recordedBy
                        }

                    let primary, values =
                        match effects with
                        | ReminderEffects.Created value
                        | ReminderEffects.Changed value -> value, [ value ]
                        | ReminderEffects.CompletedAndPlanned(completed, next) ->
                            completed, [ completed; next ]

                    let accepted =
                        values
                        |> List.map (fun value -> Reminder.id value, stored value)
                        |> Map.ofList

                    return
                        Verdict.Proceed
                            {
                                Subject =
                                    EntryTarget.Reminder
                                        {
                                            CaseId = parent.CaseId
                                            ReminderId = id
                                            Revision = Reminder.revision primary
                                            Act = ReminderCommand.kind command
                                            NextReminderId = nextId
                                            BusinessDate = services.Calendar.DateAt view.ObservedAt
                                        }
                                RequestBytes = bytes
                                SnapshotBytes = ReminderRecord.encodeEffects effects
                                Current = ReceiptCurrent.Reminder(Some parent, accepted)
                                Projection =
                                    WorkChangeProjection.Reminder
                                        {
                                            Effects = effects
                                            ChangedAt = view.ObservedAt
                                            RecordedBy = recordedBy
                                        }
                                Review =
                                    OperationReview.Reminder
                                        {
                                            Reference = parent.Snapshot.Reference
                                            Parent = parent.Snapshot
                                            CaseId = parent.CaseId
                                            Before = before
                                            Effects = effects
                                            Context =
                                                ServiceContext.runtime services view.ObservedAt
                                        }
                            }
            | other ->
                return
                    Verdict.map
                        (fun () -> invalidOp "Only a new operation has proposed effects.")
                        other
        }

    let private propose (services: ServiceContext) (tx: IReadTx) prepared =
        task {
            let! view =
                ExecutionFacts.case services tx (Some(identity prepared)) (target prepared)

            try
                match authorized services view with
                | Error refusal -> return view, Verdict.Refuse refusal
                | Ok parent ->
                    let! proposal =
                        match prepared with
                        | Work(operation, _, expected, update) ->
                            workProposal services tx view parent operation expected update
                        | Reminder(operation, _, act) ->
                            reminderProposal services tx view parent operation act

                    let entity, expected =
                        match prepared with
                        | Work(_, _, expected, _) -> ChangeEntity.Work, Revision.value expected
                        | Reminder(_, _, ReminderInput.Change(id, expected, _)) ->
                            ChangeEntity.Reminder id, Revision.value expected
                        | Reminder(operation, _, ReminderInput.Create _) ->
                            ChangeEntity.Reminder(ReminderId.ofOperationId operation), 0L

                    let! proposal =
                        ConflictReads.verdict
                            services
                            tx
                            view.Actor
                            (Some parent)
                            entity
                            expected
                            proposal

                    return view, proposal
            with :? OverflowException ->
                return view, Verdict.Fail CoreFault.InternalError
        }

    let preview (services: ServiceContext) prepared token =
        task {
            let! result =
                services.Store.Read(
                    {
                        Work =
                            fun tx ->
                                task {
                                    let! _, proposal = propose services tx prepared

                                    return
                                        proposal
                                        |> Verdict.outcome
                                            (fun value -> Previewed.Reviewed value.Review)
                                            Previewed.AlreadyAccepted
                                }
                        CancellationToken = token
                    }
                )

            return QueryResults.readResult result
        }

    let run (services: ServiceContext) prepared token =
        services.Store.Write
            {
                Work =
                    fun tx ->
                        task {
                            let! view, proposed = propose services tx prepared

                            let! fenced =
                                ExecutionFacts.fenced
                                    services
                                    tx
                                    view.Actor
                                    (identity prepared)
                                    view.ExistingOperation

                            let accepted =
                                match fenced with
                                | Error fault -> Verdict.Fail fault
                                | Ok fenced ->
                                    proposed
                                    |> Verdict.bind (fun proposal ->
                                        LedgerEnvelope.guard fenced
                                        |> Verdict.bind (fun () ->
                                            LedgerEnvelope.accept
                                                services
                                                view.Actor
                                                {
                                                    Head = tx.Head
                                                    Nonce = tx.NewPayloadNonce()
                                                    Fenced = fenced
                                                }
                                                {
                                                    OperationId = identity prepared
                                                    ObservedAt = view.ObservedAt
                                                    ActorId = LedgerEnvelope.actorId view.Actor
                                                    Subject = proposal.Subject
                                                    Snapshot = proposal.SnapshotBytes
                                                    Current = proposal.Current
                                                    Projection = proposal.Projection
                                                }
                                                proposal.RequestBytes))

                            return! ExecutionFacts.persist tx tx.ProjectWorkChange accepted
                        }
                CancellationToken = token
            }
