namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts

type internal CaseFacts =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        ExistingOperation: StoredEntry option
        Case: StoredCase option
    }

type internal AuthorityExecutionFacts =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        ExistingOperation: StoredEntry option
        Target: AuthorityView
        TargetCase: StoredCase option
        EnabledHumanOwners: int option
        ActorCount: int option
    }

module internal ExecutionFacts =
    let private operationCase (entry: StoredEntry option) =
        match entry |> Option.map _.Subject with
        | Some(EntryTarget.Case(id, _, _, _)) -> Some id
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
            | first :: rest ->
                let! initial =
                    tx.Authority(
                        {
                            AuthorityQuery.Principal = principal
                            CaseId = Some first
                        }
                    )

                let mutable actor = initial

                for id in rest do
                    let! scoped =
                        tx.Authority(
                            {
                                AuthorityQuery.Principal = principal
                                CaseId = Some id
                            }
                        )

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

    let findCase (tx: IReadTx) =
        function
        | CaseSelector.Reference reference -> tx.FindCaseByReference reference
        | CaseSelector.Id id -> tx.FindCase id

    let case (services: ServiceContext) (tx: IReadTx) operationId selector =
        let principal = services.Principal

        task {
            let! existing =
                match operationId with
                | Some id -> tx.FindEntry id
                | None -> System.Threading.Tasks.Task.FromResult None

            let! record = findCase tx selector

            let! actor =
                authority
                    tx
                    principal
                    ((record |> Option.map _.CaseId |> Option.toList)
                     @ (operationCase existing |> Option.toList))

            return
                {
                    CaseFacts.ObservedAt = tx.ObservedAt
                    Actor = actor
                    ExistingOperation = existing
                    Case = record
                }
        }

    let target (services: ServiceContext) (tx: IReadTx) needed operationId target reference =
        let principal = services.Principal

        task {
            let! existing = tx.FindEntry operationId

            let! record =
                match reference with
                | Some selector -> findCase tx selector
                | None -> System.Threading.Tasks.Task.FromResult None

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

    let persist (tx: IWriteTx) project verdict =
        task {
            match verdict with
            | Verdict.Proceed(entry, projection, receipt) ->
                do! tx.Append entry

                do! project projection
                return Verdict.Proceed receipt
            | other -> return Verdict.map (fun (_, _, receipt) -> receipt) other
        }
