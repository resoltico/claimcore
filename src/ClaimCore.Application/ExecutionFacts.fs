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
        TargetCase: StoredCase option
        EnabledHumanOwners: int option
        ActorCount: int option
    }

module internal ExecutionFacts =
    let private operationCase (entry: StoredOperation option) =
        match entry |> Option.map _.Subject with
        | Some(EntryTarget.Case(id, _, _, _)) -> Some id
        | Some(EntryTarget.Work subject) -> Some subject.CaseId
        | Some(EntryTarget.Reminder subject) -> Some subject.CaseId
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

    let target (services: ServiceContext) (tx: IReadTx) needed operationId target reference =
        let principal = services.Principal

        task {
            let! existing = tx.FindOperation operationId

            let! record =
                match reference with
                | Some selector -> findCase tx selector
                | None -> System.Threading.Tasks.Task.FromResult None

            let! maintenance = tx.Maintenance()
            let ids = record |> Option.map _.CaseId |> Option.toList
            let! actor = authority tx principal (ids @ (operationCase existing |> Option.toList))
            let! target = authority tx target ids

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
                    TargetCase = record
                    EnabledHumanOwners = owners
                    ActorCount = actors
                }
        }

    let operation (tx: IReadTx) principal id =
        task {
            let! entry = tx.FindOperation id
            let! actor = authority tx principal (operationCase entry |> Option.toList)

            let! record =
                match operationCase entry with
                | Some id -> tx.FindCase id
                | None -> System.Threading.Tasks.Task.FromResult None

            let! current =
                task {
                    match entry |> Option.map _.Subject with
                    | Some(EntryTarget.Work subject) ->
                        let! work = tx.FindWork subject.CaseId
                        return ReceiptCurrent.Work(record, work)
                    | Some(EntryTarget.Reminder subject) ->
                        let ids = subject.ReminderId :: (subject.NextReminderId |> Option.toList)
                        let mutable values = Map.empty

                        for id in ids do
                            let! value = tx.FindReminder id

                            match value with
                            | Some value -> values <- Map.add id value values
                            | None -> ()

                        return ReceiptCurrent.Reminder(record, values)
                    | Some(EntryTarget.Case _) -> return ReceiptCurrent.Case record
                    | _ -> return ReceiptCurrent.Authority
                }

            return actor, entry, current
        }

    let fenced (services: ServiceContext) (tx: IReadTx) (actor: AuthorityView) operation existing =
        match actor.Actor with
        | Some requester when Authority.validActor services.Principal actor ->
            NonAcceptance.read
                services.InstallationId
                tx
                {
                    FenceKey.ActorId = requester.ActorId
                    OperationId = operation
                }
                existing
        | _ -> System.Threading.Tasks.Task.FromResult(Ok false)

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
