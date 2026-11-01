module internal ClaimCore.TestSupport.MemoryStore

open ClaimCore.RecordFormat
open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Application
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
            Head =
                {
                    Sequence = Sequence.zero
                    EntryHash = EntryHash.zero
                }
            Entries = Map.empty
            Controls = Map.empty
            Cases = Map.empty
            Actors = Map.empty
            Grants = Set.empty
            Fences = Map.empty
        }

    let factRead =
        function
        | AuthorityFact.ActorCount -> events.Add "actor-count"
        | AuthorityFact.EnabledHumanOwners -> events.Add "enabled-owner-count"

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

    interface IStore with
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
                                    Nonce = RandomNumberGenerator.GetBytes 32
                                    Read = (fun () -> events.Add "read")
                                    FactRead = factRead
                                }
                            )
                            :> IWriteTx

                        try
                            let! verdict = work tx

                            match verdict with
                            | Verdict.Proceed _ -> state <- pending
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
