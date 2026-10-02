module ClaimCore.IntegrationTests.RuntimeConstructionTests

open System
open System.Threading.Tasks
open Expecto
open ClaimCore.Hosting
open ClaimCore.Witness

let private unwindsEveryChild () =
    let disposed = ResizeArray<int>()

    let child index fails =
        { new IDisposable with
            member _.Dispose() =
                disposed.Add(index)

                if fails then
                    invalidOp "PRIVATE-PROVIDER-CANARY"
        }

    let error =
        try
            use construction = new RuntimeConstruction()
            construction.Own(child 1 false) |> ignore
            construction.Own(child 2 true) |> ignore
            construction.Own(child 3 false) |> ignore
            invalidOp "Synthetic construction failure"
        with :? InvalidOperationException as error ->
            error

    Expect.equal (Seq.toList disposed) [ 3; 2; 1 ] "Failure attempts all children in reverse order"

    Expect.equal
        error.Message
        "ClaimCore runtime resource cleanup failed."
        "Bounded cleanup failure"

    Expect.isNull error.InnerException "No retained provider detail"

let private transfersToParent () =
    let mutable count = 0

    let child =
        { new IDisposable with
            member _.Dispose() = count <- count + 1
        }

    use construction = new RuntimeConstruction()
    let parent = construction.Own(child) |> construction.Transfer
    (construction :> IDisposable).Dispose()
    Expect.equal count 0 "Successful construction leaves the parent live"

    Expect.throwsT<ObjectDisposedException>
        (fun () -> construction.Own(child) |> ignore)
        "A transferred scope cannot acquire another child"

    parent.Dispose()
    Expect.equal count 1 "Parent owns final disposal"

let private identity: Identity =
    {
        InstallationId = Guid.Parse("70000000-0000-4000-8000-000000000001")
        LineageId = Guid.Parse("70000000-0000-4000-8000-000000000002")
        Epoch = 1L
    }

let private witnessFactory (created: Store option ref) () =
    let store =
        new Store(
            "Host=localhost;Database=synthetic;Username=claimcore_witness_writer",
            identity,
            Array.create 32 7uy
        )

    created.Value <- Some store
    store

let private refusesCapability (store: Store) =
    Expect.throwsT<InvalidOperationException>
        (fun () -> store.WithWriterCapability(fun _ -> Task.FromResult true) |> ignore)
        "Closed store cannot lend its capability"

let private custodyFailureClosesStore () =
    let created = ref None

    try
        Expect.throwsT<InvalidOperationException>
            (fun () ->
                RuntimeOpening.createWitness
                    (witnessFactory created)
                    (fun _ -> invalidOp "Synthetic custody refusal")
                    identity
                |> ignore)
            "Failed custody prevents ownership transfer"

        refusesCapability created.Value.Value
    finally
        created.Value |> Option.iter (fun store -> (store :> IDisposable).Dispose())

let private successTransfersStoreAndCustody () =
    let created = ref None
    let mutable closed = 0

    let custody =
        { new IKeyCustody with
            member _.ActiveKeyId = Guid.Parse("70000000-0000-4000-8000-000000000003")
            member _.HasKey _ = true
            member _.Encrypt(_, _, bytes) = Array.copy bytes
            member _.Decrypt(_, _, bytes) = Array.copy bytes
            member _.Dispose() = closed <- closed + 1
        }

    let witness =
        RuntimeOpening.createWitness (witnessFactory created) (fun _ -> custody) identity

    try
        Expect.isTrue
            (created.Value.Value
                .WithWriterCapability(fun _ -> Task.FromResult true)
                .GetAwaiter()
                .GetResult())
            "Successful construction retains store ownership"
    finally
        (witness :> IDisposable).Dispose()

    refusesCapability created.Value.Value
    Expect.equal closed 1 "Protocol closes its custody exactly once"

let tests =
    testList
        "runtime construction ownership"
        [
            testCase
                "[CC-RUN-001] partial construction unwinds every child despite cleanup faults"
                unwindsEveryChild
            testCase
                "[CC-RUN-001] successful construction transfers children to their parent"
                transfersToParent
            testCase
                "[CC-RUN-001] witness custody failure closes the actual store"
                custodyFailureClosesStore
            testCase
                "[CC-RUN-001] witness adoption transfers store and custody together"
                successTransfersStoreAndCustody
        ]
