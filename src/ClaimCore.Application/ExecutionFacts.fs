namespace ClaimCore.Application

open System
open ClaimCore.Domain

type internal CaseFacts =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        ExistingOperation: StoredEntry option
        Case: StoredCase option
    }

type internal AuthorityFacts =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        ExistingOperation: StoredEntry option
        Target: AuthorityView
        TargetCase: StoredCase option
        EnabledHumanOwners: int
        ActorCount: int
    }

module internal ExecutionFacts =
    let private operationCase (entry: StoredEntry option) =
        match entry |> Option.map _.Subject with
        | Some(StoredSubject.Case(id, _, _, _)) -> Some id
        | _ -> None

    let authority (tx: IReadTx) principal caseIds =
        task {
            match List.distinct caseIds with
            | [] -> return! tx.Authority(principal, None)
            | first :: rest ->
                let! initial = tx.Authority(principal, Some first)
                let mutable actor = initial

                for id in rest do
                    let! scoped = tx.Authority(principal, Some id)

                    actor <-
                        { actor with
                            Cases =
                                Map.fold
                                    (fun cases id roles -> Map.add id roles cases)
                                    actor.Cases
                                    scoped.Cases
                        }

                return actor
        }

    let case (tx: IReadTx) installation principal operationId reference =
        task {
            let! existing =
                match operationId with
                | Some id -> tx.FindEntry id
                | None -> System.Threading.Tasks.Task.FromResult None

            let id = ClaimCore.RecordFormat.CaseReferenceId.encode installation reference
            let! actor = authority tx principal (id :: (operationCase existing |> Option.toList))
            let! record = tx.FindCase id

            return
                {
                    CaseFacts.ObservedAt = tx.ObservedAt
                    Actor = actor
                    ExistingOperation = existing
                    Case = record
                }
        }

    let target (tx: IReadTx) installation principal operationId target reference =
        task {
            let! existing = tx.FindEntry operationId

            let ids =
                reference
                |> Option.map (ClaimCore.RecordFormat.CaseReferenceId.encode installation)
                |> Option.toList

            let! actor = authority tx principal (ids @ (operationCase existing |> Option.toList))
            let! target = authority tx target ids

            let! record =
                match ids with
                | id :: _ -> tx.FindCase id
                | [] -> System.Threading.Tasks.Task.FromResult None

            let! owners = tx.EnabledHumanOwners()
            let! actors = tx.ActorCount()

            return
                {
                    AuthorityFacts.ObservedAt = tx.ObservedAt
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
            let! entry = tx.FindEntry id
            let! actor = authority tx principal (operationCase entry |> Option.toList)

            let! record =
                match operationCase entry with
                | Some id -> tx.FindCase id
                | None -> System.Threading.Tasks.Task.FromResult None

            return actor, entry, record
        }

    let fenced (tx: IWriteTx) (actor: AuthorityView) operation =
        match actor.Actor with
        | None -> System.Threading.Tasks.Task.FromResult false
        | Some actor -> tx.IsFenced(actor.ActorId, operation)

    let persist (tx: IWriteTx) project verdict =
        task {
            match verdict with
            | Verdict.Proceed(entry, projection, receipt) ->
                do! tx.Append entry
                do! project projection
                return Verdict.Proceed receipt
            | other -> return Verdict.map (fun (_, _, receipt) -> receipt) other
        }
