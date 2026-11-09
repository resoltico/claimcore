namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<NoComparison>]
type internal CaseFacts =
    {
        ObservedAt: DateTimeOffset
        Maintenance: RegisterMaintenance
        Actor: AuthorityView
        Assignee: AuthorityView option
        ExistingOperation: StoredOperation option
        Case: StoredCase option
    }

[<NoComparison>]
type internal AuthorityExecutionFacts =
    {
        ObservedAt: DateTimeOffset
        Maintenance: RegisterMaintenance
        Actor: AuthorityView
        ExistingOperation: StoredOperation option
        Target: AuthorityView
        TargetRoles: Map<ActorId, ActorRoleSummary list>
        TargetCases: Map<GrantTarget, StoredCase>
        EnabledHumanOwners: int option
        ActorCount: int option
    }

module internal ExecutionFacts =
    let private operationCase (entry: StoredOperation option) =
        match entry |> Option.map _.Subject with
        | Some(EntryTarget.Case(id, _, _, _)) -> Some id
        | Some(EntryTarget.Work subject) -> Some subject.CaseId
        | Some(EntryTarget.Reminder subject) -> Some subject.CaseId
        | Some(EntryTarget.Note subject) -> Some subject.CaseId
        | _ -> None

    let authority (tx: IReadTx) principal caseIds =
        task {
            match List.distinct caseIds with
            | [] ->
                return!
                    tx.Authority(
                        {
                            AuthorityQuery.Principal = principal
                            CaseId = None
                        }
                    )
            | ids -> return! tx.AuthorityCases(principal, ids)
        }

    let findCase (tx: IReadTx) =
        function
        | CaseSelector.Reference reference -> tx.FindCaseByReference reference
        | CaseSelector.Id id -> tx.FindCase id

    let case (services: ServiceContext) (tx: IReadTx) operationId selector =
        let principal = services.Principal

        task {
            let! existing =
                match operationId with
                | Some id -> tx.FindOperation id
                | None -> System.Threading.Tasks.Task.FromResult None

            let! record = findCase tx selector
            let! maintenance = tx.Maintenance()

            let! actor =
                authority
                    tx
                    principal
                    ((record |> Option.map _.CaseId |> Option.toList)
                     @ (operationCase existing |> Option.toList))

            return
                {
                    CaseFacts.Assignee = None
                    Maintenance = maintenance
                    ObservedAt = tx.ObservedAt
                    Actor = actor
                    ExistingOperation = existing
                    Case = record
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
            let ids = records |> Map.values |> Seq.map _.CaseId |> Seq.distinct |> Seq.toList
            let! actor = authority tx principal (ids @ (operationCase existing |> Option.toList))
            let! target = authority tx target ids

            let! targetRoles =
                tx.ActorRoleSummaries(target.Actor |> Option.map _.ActorId |> Option.toList)

            let! owners =
                if List.contains AuthorityFact.EnabledHumanOwners needed then
                    task { let! count = tx.EnabledHumanOwners() in return Some count }
                else
                    System.Threading.Tasks.Task.FromResult None

            let! actors =
                if List.contains AuthorityFact.ActorCount needed then
                    task { let! count = tx.ActorCount() in return Some count }
                else
                    System.Threading.Tasks.Task.FromResult None

            return
                {
                    AuthorityExecutionFacts.ObservedAt = tx.ObservedAt
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
        authority tx principal (operationCase entry |> Option.toList)

    let receiptCurrent (tx: IReadTx) entry =
        task {
            let! record =
                match operationCase entry with
                | Some id -> tx.FindCase id
                | None -> System.Threading.Tasks.Task.FromResult None

            let! current =
                task {
                    match entry |> Option.map _.Subject with
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
                    | _ -> return ReceiptCurrent.Authority None
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

    /// A visible or hidden acceptance is handled by the existing replay/disclosure policy first.
    let lifetime (services: ServiceContext) actor existing observedAt operation =
        if
            existing |> Option.isNone
            && Authority.validActor services.Principal actor
            && not (OperationLifetime.admits observedAt operation)
        then
            Some(Refusal.OperationIdOutOfWindow(OperationId.issuedAt operation, observedAt))
        else
            None

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

    /// One verdict commits every effect and projection together, or the store rolls all of them back.
    let persistOperation
        (tx: IWriteTx)
        (project: 'projection -> System.Threading.Tasks.Task<unit>)
        (verdict: Verdict<EntryDraft list * 'projection list * OperationReceipt>)
        =
        task {
            match verdict with
            | Verdict.Proceed(entries, projections, receipt) ->
                let group = StoredOperation.create (entries |> List.map LedgerEntries.stored)

                match group with
                | Error fault -> return Verdict.Fail fault
                | Ok _ when entries.Length <> projections.Length ->
                    return Verdict.Fail CoreFault.InternalError
                | Ok _ ->
                    return!
                        whenWritable tx (fun () ->
                            task {
                                for entry, projection in List.zip entries projections do
                                    do! tx.Append entry
                                    do! project projection

                                return Verdict.Proceed receipt
                            })
            | other -> return Verdict.map (fun (_, _, receipt) -> receipt) other
        }
