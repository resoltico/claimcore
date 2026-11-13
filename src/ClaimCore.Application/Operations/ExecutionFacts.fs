namespace ClaimCore.Application

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
    }

module internal ExecutionFacts =
    let private operationCase (entry: RetainedOperation option) =
        match entry |> Option.map (fun operation -> (LedgerEntries.primary operation).Subject) with
        | Some(EntryTarget.Case(id, _, _, _)) -> Some id
        | Some(EntryTarget.Work subject) -> Some subject.CaseId
        | Some(EntryTarget.Reminder subject) -> Some subject.CaseId
        | Some(EntryTarget.Note subject) -> Some subject.CaseId
        | _ -> None

    let people (tx: IReadTx) principal caseIds =
        task {
            match List.distinct caseIds with
            | [] ->
                return!
                    tx.Access(
                        {
                            AccessQuery.Principal = principal
                            CaseId = None
                        }
                    )
            | ids -> return! tx.AccessCases(principal, ids)
        }

    let findCase (tx: IReadTx) =
        function
        | CaseSelector.Reference reference -> tx.FindCaseByReference reference
        | CaseSelector.Id id -> tx.FindCase id

    let case
        (services: ServiceContext)
        (tx: IReadTx)
        operationId
        selector
        (paymentAct: AuthoredCaseAct option)
        =
        let principal = services.Principal

        task {
            let! existing =
                match operationId with
                | Some id -> tx.FindOperation id
                | None -> System.Threading.Tasks.Task.FromResult None

            let! record = findCase tx selector
            let! maintenance = tx.Maintenance()
            let! frontier = tx.ExpiredBefore()

            let! actor =
                people
                    tx
                    principal
                    ((record |> Option.map _.CaseId |> Option.toList)
                     @ (operationCase existing |> Option.toList))

            let! state =
                match record, paymentAct with
                | Some current, Some act when
                    Access.canAct principal actor (Scope.Case current.CaseId) act
                    ->
                    tx.CaseState current.CaseId
                | _ -> System.Threading.Tasks.Task.FromResult None

            let! work =
                match state with
                | Some state -> tx.FindWork state.CaseId
                | None -> System.Threading.Tasks.Task.FromResult None

            let! pendingDue =
                match state with
                | Some state -> tx.PendingReminderDue(state.CaseId, None)
                | None -> System.Threading.Tasks.Task.FromResult []

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
                    Work = work
                    PendingReminderDue = pendingDue
                }
        }

    let target (services: ServiceContext) (tx: IReadTx) needed operationId target references =
        let principal = services.Principal

        task {
            let! existing = tx.FindOperation operationId

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

            let! maintenance = tx.Maintenance()
            let! frontier = tx.ExpiredBefore()
            let ids = records |> Map.values |> Seq.map _.CaseId |> Seq.distinct |> Seq.toList
            let! actor = people tx principal (ids @ (operationCase existing |> Option.toList))
            let! target = people tx target ids

            let! targetRoles =
                tx.ActorRoleSummaries(target.Actor |> Option.map _.ActorId |> Option.toList)

            let! owners =
                if List.contains PeopleFact.EnabledHumanOwners needed then
                    task { let! count = tx.EnabledHumanOwners() in return Some count }
                else
                    System.Threading.Tasks.Task.FromResult None

            let! actors =
                if List.contains PeopleFact.ActorCount needed then
                    task { let! count = tx.ActorCount() in return Some count }
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
                }
        }

    let operationActor (tx: IReadTx) principal entry =
        people tx principal (operationCase entry |> Option.toList)

    let receiptCurrent (tx: IReadTx) entry =
        task {
            let! record =
                match operationCase entry with
                | Some id -> tx.FindCase id
                | None -> System.Threading.Tasks.Task.FromResult None

            let! current =
                task {
                    match
                        entry
                        |> Option.map (fun operation -> (LedgerEntries.primary operation).Subject)
                    with
                    | Some(EntryTarget.Work subject) ->
                        let! work = tx.FindWork subject.CaseId
                        return ReceiptCurrent.Work(record, work, Map.empty)
                    | Some(EntryTarget.Reminder subject) ->
                        let ids = subject.ReminderId :: (subject.NextReminderId |> Option.toList)
                        let mutable values = Map.empty

                        for id in ids do
                            let! value = tx.FindReminder id

                            match value with
                            | Some value -> values <- Map.add id value values
                            | None -> ()

                        return ReceiptCurrent.Reminder(record, values)
                    | Some(EntryTarget.Note subject) ->
                        let! note = tx.FindNote subject.NoteId
                        return ReceiptCurrent.Note(record, note |> Option.map _.Note)
                    | Some(EntryTarget.Case _) -> return ReceiptCurrent.Case record
                    | _ -> return ReceiptCurrent.People None
                }

            return current
        }

    let operation (tx: IReadTx) principal id =
        task {
            let! entry = tx.FindOperation id
            let! actor = operationActor tx principal entry
            let! current = receiptCurrent tx entry
            return actor, entry, current
        }

    let whenWritable (tx: IWriteTx) action =
        task {
            let! mode = tx.Maintenance()

            if mode.Active then
                return Verdict.Refuse Refusal.RegisterMaintenance
            else
                return! action ()
        }

    let persist (tx: IWriteTx) project verdict =
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

    /// One verdict appends the closed root once and projects every logical effect atomically.
    let persistOperation
        (tx: IWriteTx)
        project
        (verdict: Verdict<RetainedOperation * 'projection list * OperationReceipt>)
        =
        task {
            match verdict with
            | Verdict.Proceed(operation, projections, receipt) ->
                return!
                    whenWritable tx (fun () ->
                        task {
                            do! tx.Append operation

                            for projection in projections do
                                do! project projection

                            return Verdict.Proceed receipt
                        })
            | other -> return Verdict.map (fun (_, _, receipt) -> receipt) other
        }
