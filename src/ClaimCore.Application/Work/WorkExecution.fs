namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Work families share ledger identity, delivery and authorization; their business decisions stay typed.
module internal WorkExecution =
    type Prepared =
        private
        | Work of OperationId * CaseSelector * Revision * ValidCaseWorkUpdate
        | Reminder of OperationId * CaseSelector * ReminderInput
        | Note of OperationId * CaseSelector * NoteInput

    [<NoComparison>]
    type private Proposal =
        {
            Command: CommandSkeleton
            Values: OperationValues
            Current: ReceiptCurrent
            Projection: WorkChangeProjection
            Index: (CaseId * CaseIndex) option
            Review: OperationReview
        }

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

    let private ordered result =
        result
        |> Result.mapError (function
            | Refusal.InvalidInput issues ->
                InputIssues.toList issues
                |> List.sortBy (fun issue -> InputTarget.order issue.Target)
                |> InputIssues.ofList
                |> Option.get
                |> Refusal.InvalidInput
            | refusal -> refusal)

    let prepareWork (request: WorkOperationRequest) =
        let (WorkAct.UpdateWork raw) = request.Act

        let update =
            CaseWorkInput.admit raw
            |> Result.mapError (fun issues ->
                issues
                |> List.map (fun (field, violation) ->
                    {
                        Target = InputTarget.Field field
                        GrantRow = None
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
        |> ordered

    let prepareReminder (request: ReminderOperationRequest) =
        Admission.map2
            (fun operation act -> Reminder(operation, request.Target, act))
            (operation request.OperationId)
            (ReminderInput.admit request.Act)
        |> fun prepared ->
            Admission.map2
                (fun result () -> result)
                prepared
                (CaseExecution.validateSelector request.Target)
        |> ordered

    let prepareNote (request: NoteOperationRequest) =
        Admission.map2
            (fun operation act -> Note(operation, request.Target, act))
            (operation request.OperationId)
            (NoteInput.admit request.Act)
        |> fun prepared ->
            Admission.map2
                (fun result () -> result)
                prepared
                (CaseExecution.validateSelector request.Target)
        |> ordered

    let private target =
        function
        | Work(_, target, _, _)
        | Reminder(_, target, _)
        | Note(_, target, _) -> target

    let private identity =
        function
        | Work(operation, _, _, _)
        | Reminder(operation, _, _)
        | Note(operation, _, _) -> operation

    let private authorized (services: ServiceContext) (view: CaseFacts) =
        match view.Case with
        | Some parent ->
            CaseWorkPolicy.authorize services.Principal view.Actor parent.CaseId
            |> Result.map (fun () -> parent)
        | None -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)

    let private eligibleAssignee (tx: IReadTx) caseId =
        function
        | None -> System.Threading.Tasks.Task.FromResult true
        | Some id ->
            task {
                let! actor = tx.FindActor id

                match actor with
                | Some actor when PrincipalKey.isHuman actor.Principal ->
                    let! people =
                        tx.Access
                            {
                                Principal = actor.Principal
                                CaseId = Some caseId
                            }

                    return CaseWorkPolicy.eligibleAssignee actor.Principal people caseId
                | _ -> return false
            }

    let private expiry executing (view: CaseFacts) operation =
        if executing then
            LedgerEnvelope.lifetime view.ExpiredBefore view.ObservedAt operation
        else
            LedgerEnvelope.frontier view.ExpiredBefore view.ObservedAt operation

    let private workProposal
        executing
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

            let bytes = OperationIntent.work operation parent.CaseId expected update
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

            let replay = replay |> Verdict.bind (fun () -> expiry executing view operation)

            match replay with
            | Verdict.Proceed() ->
                match CaseWorkPolicy.parentRefusal parent.Snapshot, current with
                | Some refusal, _ -> return Verdict.Refuse(Refusal.Case refusal)
                | None, Some current when current.Snapshot.IsSome ->
                    let before = current.Snapshot.Value

                    if view.Maintenance.Active then
                        return Verdict.Refuse Refusal.RegisterMaintenance
                    elif before.Revision <> expected then
                        return Verdict.Refuse(Refusal.revisionConflict before.Revision)
                    else
                        let! eligible =
                            eligibleAssignee
                                tx
                                parent.CaseId
                                (match update.Responsible with
                                 | WorkValue.Set id -> Some id
                                 | _ -> None)

                        match
                            CaseWorkDecision.decide parent.Snapshot eligible expected update before
                        with
                        | Error refusal -> return Verdict.Refuse refusal
                        | Ok after ->
                            let! pendingDue = tx.PendingReminderDue(parent.CaseId, None)
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
                                            Command = CommandSkeleton.Work(parent.CaseId, expected)
                                            Values =
                                                OperationValues.Work
                                                    { Update = update; After = after }
                                            Current =
                                                ReceiptCurrent.Work(
                                                    Some parent,
                                                    Some stored,
                                                    accounts
                                                )
                                            Index =
                                                Some(
                                                    parent.CaseId,
                                                    CaseIndex.create
                                                        parent.Index.ChangedAt
                                                        parent.Snapshot
                                                        (Some after)
                                                        pendingDue
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
                    fields.Status, CaseProgress.ofFinancial fields.Finance)

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
        executing
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
                | ReminderInput.Create plan -> ReminderCommand.Create(parent.CaseId, plan)
                | ReminderInput.Change(id, revision, change) ->
                    ReminderCommand.Change(parent.CaseId, id, revision, change)

            let target, expected =
                match command with
                | ReminderCommand.Create _ -> None, None
                | ReminderCommand.Change(_, target, expected, _) -> Some target, Some expected

            let scope =
                {
                    ReminderCommandSkeleton.CaseId = parent.CaseId
                    Act = ReminderCommand.kind command
                    ReminderId = target
                    ExpectedRevision = expected
                }

            let bytes = OperationIntent.reminder operation scope command
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

            let replay = replay |> Verdict.bind (fun () -> expiry executing view operation)

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
                    let! work = tx.FindWork parent.CaseId
                    let! pendingDue = tx.PendingReminderDue(parent.CaseId, Some id)
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

                    let _primary, values =
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
                                Command = CommandSkeleton.Reminder scope
                                Values =
                                    OperationValues.Reminder
                                        { Command = command; Effects = effects }
                                Current = ReceiptCurrent.Reminder(Some parent, accepted)
                                Index =
                                    Some(
                                        parent.CaseId,
                                        CaseIndex.create
                                            parent.Index.ChangedAt
                                            parent.Snapshot
                                            (work |> Option.bind _.Snapshot)
                                            (pendingDue
                                             @ (values
                                                |> List.filter (fun value ->
                                                    Reminder.state value = ReminderState.Pending)
                                                |> List.map Reminder.due))
                                    )
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

    let private noteProposal
        executing
        (services: ServiceContext)
        (tx: IReadTx)
        (view: CaseFacts)
        (parent: StoredCase)
        operation
        input
        =
        task {
            let command = NoteInput.resolve parent.CaseId input
            let id = NoteCommand.target operation command

            let bytes = OperationIntent.note operation command
            let! stored = tx.FindNote id
            let before = stored |> Option.map _.Note

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
                        (ReceiptCurrent.Note(Some parent, before)))

            let replay = replay |> Verdict.bind (fun () -> expiry executing view operation)

            match replay with
            | Verdict.Proceed() when view.Maintenance.Active ->
                return Verdict.Refuse Refusal.RegisterMaintenance
            | Verdict.Proceed() ->
                match Note.decide operation parent.Snapshot before command with
                | Error NoteRefusal.IdentityReserved ->
                    return Verdict.Fail CoreFault.StoreIntegrityError
                | Error(NoteRefusal.InvalidText violation) ->
                    return Verdict.Refuse(Refusal.invalidInput (InputTarget.NoteText, violation))
                | Error NoteRefusal.ParentMismatch
                | Error NoteRefusal.NotOffered ->
                    return Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
                | Error NoteRefusal.ParentNotActive ->
                    let state =
                        parent.Snapshot.State
                        |> CaseState.map (fun fields ->
                            fields.Status, CaseProgress.ofFinancial fields.Finance)

                    return
                        Verdict.Refuse(
                            Refusal.Case(CaseRefusal.NotOffered(CaseHeadline.ofState state))
                        )
                | Ok after ->
                    return
                        Verdict.Proceed
                            {
                                Command =
                                    match command with
                                    | NoteCommand.AddNote(parent, _) ->
                                        CommandSkeleton.Note(NoteCommandSkeleton.Add parent)
                                    | NoteCommand.RemoveNote(parent, id) ->
                                        CommandSkeleton.Note(NoteCommandSkeleton.Remove(parent, id))
                                Values = OperationValues.Note { Command = command; After = after }
                                Current = ReceiptCurrent.Note(Some parent, Some after)
                                Index = None
                                Projection = WorkChangeProjection.Note after
                                Review =
                                    OperationReview.Note
                                        {
                                            Parent = parent.Snapshot
                                            Reference = parent.Snapshot.Reference
                                            Before = before
                                            Proposed = after
                                            Act = NoteCommand.kind command
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

    let private propose executing (services: ServiceContext) (tx: IReadTx) prepared =
        task {
            let! view =
                ExecutionFacts.case services tx (Some(identity prepared)) (target prepared) None

            try
                match authorized services view with
                | Error refusal -> return view, Verdict.Refuse refusal
                | Ok parent ->
                    let! proposal =
                        match prepared with
                        | Work(operation, _, expected, update) ->
                            workProposal executing services tx view parent operation expected update
                        | Reminder(operation, _, act) ->
                            reminderProposal executing services tx view parent operation act
                        | Note(operation, _, act) ->
                            noteProposal executing services tx view parent operation act

                    let conflict =
                        match prepared with
                        | Work(_, _, expected, _) ->
                            Some(ChangeEntity.Work, Revision.value expected)
                        | Reminder(_, _, ReminderInput.Change(id, expected, _)) ->
                            Some(ChangeEntity.Reminder id, Revision.value expected)
                        | Reminder(operation, _, ReminderInput.Create _) ->
                            Some(ChangeEntity.Reminder(ReminderId.ofOperationId operation), 0L)
                        | Note _ -> None

                    let! proposal =
                        match conflict with
                        | None -> System.Threading.Tasks.Task.FromResult proposal
                        | Some(entity, expected) ->
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
                                    let! _, proposal = propose false services tx prepared

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
                            let! view, proposed = propose true services tx prepared

                            let accepted =
                                proposed
                                |> Verdict.bind (fun proposal ->
                                    LedgerEnvelope.accept
                                        services
                                        view.Actor
                                        {
                                            Head = tx.Head
                                            Nonce = tx.NewPayloadNonce()
                                        }
                                        {
                                            OperationId = identity prepared
                                            ObservedAt = view.ObservedAt
                                            ActorId = LedgerEnvelope.actorId view.Actor
                                            Command = proposal.Command
                                            Values = proposal.Values
                                            Current = proposal.Current
                                            Projection = proposal.Projection, proposal.Index
                                        })

                            let write (projection, index) =
                                task {
                                    do! tx.ProjectWorkChange projection

                                    match index with
                                    | Some(id, index) -> do! tx.ProjectCaseIndex(id, index)
                                    | None -> ()
                                }

                            return! ExecutionFacts.persist tx write accepted
                        }
                CancellationToken = token
            }
