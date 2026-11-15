namespace ClaimCore.Application.Execution

open ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application

open System
open ClaimCore.RecordFormat
open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<NoComparison>]
type internal CaseFacts =
    {
        ObservedAt: DateTimeOffset
        ExpiredBefore: DateTimeOffset option
        Maintenance: RegisterMaintenance
        Actor: AccessView
        Assignee: AccessView option
        ExistingOperation: RetainedOperation option
        Case: StoredCase option
        State: CaseSubjectState option
        FamilySequence: Sequence
        Family: CaseFamilyState option
        Work: StoredWork option
        PendingReminderDue: CalendarDate list
    }

[<NoComparison>]
type internal PeopleExecutionFacts =
    {
        ObservedAt: DateTimeOffset
        ExpiredBefore: DateTimeOffset option
        Maintenance: RegisterMaintenance
        Actor: AccessView
        ExistingOperation: RetainedOperation option
        Target: AccessView
        TargetRoles: Map<ActorId, ActorRoleSummary list>
        TargetCases: Map<GrantTarget, StoredCase>
        EnabledHumanOwners: int option
        ActorCount: int option
        State: PeopleSubjectState option
    }

module internal ExecutionFacts =
    let private operationCase (entry: RetainedOperation option) =
        match entry |> Option.map (fun operation -> (LedgerEntries.primary operation).Subject) with
        | Some(EntryTarget.Case(id, _, _, _)) -> Some id
        | Some(EntryTarget.Work subject) -> Some subject.CaseId
        | Some(EntryTarget.Reminder subject) -> Some subject.CaseId
        | Some(EntryTarget.Note subject) -> Some subject.CaseId
        | _ -> None

    let people (tx: ReadTx) principal caseIds =
        task {
            match List.distinct caseIds with
            | [] -> return! AccessReads.access tx (principal) (None)
            | ids -> return! AccessReads.accessCases tx (principal, ids)
        }

    let findCase (tx: ReadTx) =
        function
        | CaseSelector.Reference reference -> FamilyReads.findCaseByReference tx reference
        | CaseSelector.Id id -> FamilyReads.findCase tx id

    let case
        (services: ServiceContext)
        (tx: ReadTx)
        operationId
        selector
        (paymentAct: AuthoredCaseAct option)
        =
        let principal = services.Principal

        task {
            let! existing =
                match operationId with
                | Some id -> LedgerReads.findOperation tx id
                | None -> System.Threading.Tasks.Task.FromResult None

            let! record = findCase tx selector

            let! familySequence =
                match record with
                | Some record -> FamilyReads.caseFamilySequence tx record.CaseId
                | None -> System.Threading.Tasks.Task.FromResult Sequence.zero

            let! maintenance = RegisterReads.maintenance tx ()
            let! frontier = RegisterReads.expiredBefore tx ()

            let! actor =
                people
                    tx
                    principal
                    ((record |> Option.map _.CaseId |> Option.toList)
                     @ (operationCase existing |> Option.toList))

            let! state =
                match record, paymentAct with
                | Some current, Some act when
                    existing.IsNone && Access.canAct principal actor (Scope.Case current.CaseId) act
                    ->
                    FamilyReads.caseState tx current.CaseId
                | _ -> System.Threading.Tasks.Task.FromResult None

            let! work =
                match state with
                | Some state -> WorkReads.findWork tx state.CaseId
                | None -> System.Threading.Tasks.Task.FromResult None

            let! pendingDue =
                match state with
                | Some state -> ReminderReads.pendingReminderDue tx (state.CaseId, None)
                | None -> System.Threading.Tasks.Task.FromResult []

            let! family =
                match state, paymentAct with
                | Some parent, Some(CaseAct.Bare BareAct.Erase) when
                    parent.Lifecycle <> Lifecycle.Erased
                    ->
                    FamilyReads.caseFamily tx parent.CaseId
                | _ -> System.Threading.Tasks.Task.FromResult None

            return
                {
                    CaseFacts.Assignee = None
                    Maintenance = maintenance
                    ObservedAt = tx.ObservedAt
                    ExpiredBefore = frontier
                    Actor = actor
                    ExistingOperation = existing
                    Case = record
                    State = state
                    FamilySequence = familySequence
                    Family = family
                    Work = work
                    PendingReminderDue = pendingDue
                }
        }

    let target
        (services: ServiceContext)
        (tx: ReadTx)
        needed
        operationId
        targetPrincipal
        references
        =
        let principal = services.Principal

        task {
            let! existing = LedgerReads.findOperation tx operationId

            let mutable records = Map.empty

            for reference in List.distinct references do
                let selector =
                    match reference with
                    | GrantTarget.CaseReference value -> Some(CaseSelector.Reference value)
                    | GrantTarget.CaseId value -> Some(CaseSelector.Id value)
                    | GrantTarget.Installation -> None

                match selector with
                | Some selector ->
                    let! record = findCase tx selector

                    match record with
                    | Some record -> records <- Map.add reference record records
                    | None -> ()
                | None -> ()

            let! maintenance = RegisterReads.maintenance tx ()
            let! frontier = RegisterReads.expiredBefore tx ()
            let ids = records |> Map.values |> Seq.map _.CaseId |> Seq.distinct |> Seq.toList
            let! actor = people tx principal (ids @ (operationCase existing |> Option.toList))
            let! target = people tx targetPrincipal ids

            let! targetRoles =
                PeopleReads.actorRoleSummaries
                    tx
                    (target.Actor |> Option.map _.ActorId |> Option.toList)

            let! owners =
                if List.contains PeopleFact.EnabledHumanOwners needed then
                    task { let! count = PeopleReads.enabledHumanOwners tx () in return Some count }
                else
                    System.Threading.Tasks.Task.FromResult None

            let! actors =
                if List.contains PeopleFact.ActorCount needed then
                    task { let! count = PeopleReads.actorCount tx () in return Some count }
                else
                    System.Threading.Tasks.Task.FromResult None

            let! state =
                if
                    existing.IsNone
                    && (List.contains PeopleFact.ActorCount needed
                        || Access.can principal actor Capability.ManageAccess Scope.Installation)
                then
                    PeopleReads.peopleState tx targetPrincipal
                else
                    System.Threading.Tasks.Task.FromResult None

            return
                {
                    PeopleExecutionFacts.ObservedAt = tx.ObservedAt
                    ExpiredBefore = frontier
                    Maintenance = maintenance
                    Actor = actor
                    ExistingOperation = existing
                    Target = target
                    TargetRoles = targetRoles
                    TargetCases = records
                    EnabledHumanOwners = owners
                    ActorCount = actors
                    State = state
                }
        }

    let operationActor (tx: ReadTx) principal entry =
        people tx principal (operationCase entry |> Option.toList)

    let receiptCurrent (tx: ReadTx) entry =
        task {
            let! record =
                match operationCase entry with
                | Some id -> FamilyReads.findCase tx id
                | None -> System.Threading.Tasks.Task.FromResult None

            let! current =
                task {
                    match
                        entry
                        |> Option.map (fun operation -> (LedgerEntries.primary operation).Subject)
                    with
                    | Some(EntryTarget.Work subject) ->
                        let! work = WorkReads.findWork tx subject.CaseId
                        return ReceiptCurrent.Work(record, work, Map.empty)
                    | Some(EntryTarget.Reminder subject) ->
                        let ids = subject.ReminderId :: (subject.NextReminderId |> Option.toList)
                        let mutable values = Map.empty

                        for id in ids do
                            let! value = ReminderReads.findReminder tx id

                            match value with
                            | Some value -> values <- Map.add id value values
                            | None -> ()

                        return ReceiptCurrent.Reminder(record, values)
                    | Some(EntryTarget.Note subject) ->
                        let! note = NoteReads.findNote tx subject.NoteId
                        return ReceiptCurrent.Note(record, note |> Option.map _.Note)
                    | Some(EntryTarget.Case _) -> return ReceiptCurrent.Case record
                    | _ -> return ReceiptCurrent.People None
                }

            return current
        }

    let operation (tx: ReadTx) principal id =
        task {
            let! entry = LedgerReads.findOperation tx id
            let! actor = operationActor tx principal entry
            let! current = receiptCurrent tx entry
            return actor, entry, current
        }

    let whenWritable (tx: WriteTx) action =
        task {
            let! mode = RegisterReads.maintenance tx ()

            if mode.Active then
                return Verdict.Refuse Refusal.RegisterMaintenance
            else
                return! action ()
        }

    let persist (tx: WriteTx) project verdict =
        task {
            match verdict with
            | Verdict.Proceed(entry, projection, receipt) ->
                return!
                    whenWritable tx (fun () ->
                        task {
                            do! tx.Append entry
                            do! project projection
                            return Verdict.Proceed receipt
                        })
            | other -> return Verdict.map (fun (_, _, receipt) -> receipt) other
        }
