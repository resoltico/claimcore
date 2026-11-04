module internal ClaimCore.TestSupport.MemoryStore

open ClaimCore.RecordFormat
open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Domain

open ClaimCore.TestSupport.MemoryStoreState
open ClaimCore.TestSupport.MemoryReadTransaction
open ClaimCore.TestSupport.MemoryWriteTransaction

type Store(installationId: Guid, observed: DateTimeOffset) =
    let gate = new SemaphoreSlim(1, 1)
    let events = ResizeArray<string>()

    let mutable state =
        {
            Maintenance = RegisterMaintenance.initial
            Head =
                {
                    Sequence = Sequence.zero
                    EntryHash = EntryHash.zero
                }
            Entries = Map.empty
            Controls = Map.empty
            Cases = Map.empty
            Work = Map.empty
            Reminders = Map.empty
            Requests = Map.empty
            Actors = Map.empty
            Grants = Set.empty
            Fences = Map.empty
        }

    let factRead =
        function
        | AuthorityFact.ActorCount -> events.Add "actor-count"
        | AuthorityFact.EnabledHumanOwners -> events.Add "enabled-owner-count"

    member _.ChangeMaintenance(change) =
        task {
            do! gate.WaitAsync()

            try
                let proposed =
                    match change with
                    | MaintenanceChange.Enter -> RegisterMaintenance.enter state.Maintenance
                    | MaintenanceChange.Resume generation ->
                        RegisterMaintenance.resume generation state.Maintenance

                match proposed with
                | Ok after -> state <- { state with Maintenance = after }
                | Error _ -> ()

                return proposed
            finally
                gate.Release() |> ignore
        }

    member val ObservedAt = observed with get, set

    member val ReadFailure: StoreFailure option = None with get, set
    member val WriteFailure: StoreFailure option = None with get, set
    member val Calls = 0 with get, set
    member _.Events = List.ofSeq events

    member _.SeedActor (actor: Actor) grants =
        state <-
            { state with
                Actors = Map.add actor.ActorId actor state.Actors
                Grants =
                    grants
                    |> List.fold
                        (fun all (role, scope) -> Set.add (actor.ActorId, role, scope) all)
                        state.Grants
            }

    member _.AccessRequests = state.Requests |> Map.values |> Seq.toList

    interface IStore with
        member this.Context query =
            task {
                this.Calls <- this.Calls + 1

                match this.ReadFailure with
                | Some failure -> return Error failure
                | None ->
                    do! gate.WaitAsync query.CancellationToken

                    try
                        let reader =
                            Reader(
                                (fun () -> state),
                                installationId,
                                this.ObservedAt,
                                (fun () -> events.Add "read"),
                                factRead = factRead
                            )
                            :> IReadTx

                        let! facts = reader.GlobalReadFacts query.Principal

                        if facts.Authority.Actor.IsNone then
                            let expired =
                                state.Requests
                                |> Map.toList
                                |> List.filter (fun (_, value) ->
                                    value.LastSeen < this.ObservedAt.AddDays(-30))
                                |> List.sortBy (fun (key, value) -> value.LastSeen, key)
                                |> List.truncate 50

                            let requests =
                                expired
                                |> List.fold (fun acc (key, _) -> Map.remove key acc) state.Requests

                            let previous = Map.tryFind query.Principal requests

                            let first =
                                previous
                                |> Option.map _.FirstSeen
                                |> Option.defaultValue this.ObservedAt

                            let last =
                                previous
                                |> Option.map (fun value -> max value.LastSeen this.ObservedAt)
                                |> Option.defaultValue this.ObservedAt

                            let request =
                                {
                                    AccessRequest.Principal = query.Principal
                                    FirstSeen = first
                                    LastSeen = last
                                    Claims = query.Claims
                                }

                            let requests =
                                Map.add query.Principal request requests
                                |> Map.toList
                                |> List.sortByDescending (fun (key, value) -> value.LastSeen, key)
                                |> List.truncate 1000
                                |> Map.ofList

                            state <- { state with Requests = requests }

                        return
                            Ok
                                {
                                    ContextFacts.Facts = facts
                                    ObservedAt = this.ObservedAt
                                }
                    finally
                        gate.Release() |> ignore
            }

        member this.Read
            ({
                 ReadOperation.Work = work
                 CancellationToken = token
             })
            =
            task {
                this.Calls <- this.Calls + 1

                match this.ReadFailure with
                | Some failure -> return Error failure
                | None ->
                    do! gate.WaitAsync token

                    try
                        let snapshot = state

                        try
                            let! result =
                                work (
                                    Reader(
                                        (fun () -> snapshot),
                                        installationId,
                                        this.ObservedAt,
                                        (fun () -> events.Add "read"),
                                        factRead = factRead
                                    )
                                    :> IReadTx
                                )

                            return Ok result
                        with
                        | CorruptProjection -> return Error StoreFailure.Integrity
                        | :? OperationCanceledException -> return Error StoreFailure.Cancelled
                    finally
                        gate.Release() |> ignore
            }

        member this.Write
            ({
                 WriteOperation.Work = work
                 CancellationToken = token
             })
            =
            task {
                this.Calls <- this.Calls + 1

                match this.WriteFailure with
                | Some failure -> return Error failure
                | None ->
                    do! gate.WaitAsync token

                    try
                        events.Clear()
                        events.Add "lock"
                        let mutable pending = state

                        let tx =
                            Writer(
                                {
                                    Get = (fun () -> pending)
                                    Set = (fun value -> pending <- value)
                                    InstallationId = installationId
                                    Observed = this.ObservedAt
                                    Head = state.Head
                                    Read = (fun () -> events.Add "read")
                                    FactRead = factRead
                                    Trace = events.Add
                                }
                            )
                            :> IWriteTx

                        try
                            let! verdict = work tx

                            match verdict with
                            | Verdict.Proceed _ ->
                                if not (completeOperations state.Head.Sequence pending) then
                                    raise CorruptProjection

                                state <- pending
                            | Verdict.Replay _
                            | Verdict.Refuse _
                            | Verdict.Fail _ -> ()

                            return Ok verdict
                        with
                        | CorruptProjection -> return Error StoreFailure.Integrity
                        | :? OperationCanceledException -> return Error StoreFailure.Cancelled
                        | _ -> return Error StoreFailure.Internal
                    finally
                        gate.Release() |> ignore
            }

    interface IDisposable with
        member _.Dispose() = gate.Dispose()
