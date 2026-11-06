module ClaimCore.Tests.MemoryStoreTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private fixture () =
    let store =
        new MemoryStore.Store(id 500, DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero))

    Task.FromResult
        {
            Store = store :> IStore
            InstallationId = id 500
            Owner =
                PrincipalKey.human "https://issuer.example/" "owner"
                |> Result.defaultWith (string >> failtest)
            Calendar =
                { new IBusinessCalendar with
                    member _.TimeZoneId = "Etc/UTC"

                    member _.DateAt instant =
                        DateOnly.FromDateTime instant.UtcDateTime
                }
            Dispose = fun () -> (store :> IDisposable).Dispose()
        }

let private rollback () =
    async {
        use memory = new MemoryStore.Store(id 500, DateTimeOffset.UtcNow)
        let store = memory :> IStore

        let principal =
            PrincipalKey.human "https://issuer.example/" "rollback"
            |> Result.defaultWith (string >> failtest)

        for verdict in
            [
                Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
                Verdict.Fail ClaimCore.Application.Contracts.CoreFault.InternalError
            ] do
            let! result =
                store.Write(
                    {
                        WriteOperation.Work =
                            (fun tx ->
                                task {
                                    let! _ = tx.ActorCount()

                                    do!
                                        tx.ProjectAuthority(
                                            AuthorityProjection.Register(
                                                (CaseFixtures.actorId (id 800)),
                                                principal
                                            )
                                        )

                                    return verdict
                                })
                        CancellationToken = CancellationToken.None
                    }
                )
                |> Async.AwaitTask

            Expect.equal result (Ok verdict) "Verdict survives rollback"

            Expect.equal
                (List.head memory.Events)
                "lock"
                "Write capability is created after locking"

            let! count =
                store.Read(
                    {
                        ReadOperation.Work = (fun tx -> tx.ActorCount())
                        CancellationToken = CancellationToken.None
                    }
                )
                |> Async.AwaitTask

            Expect.equal count (Ok 0) "Rejected staged projection is discarded"
    }

let private requestExpiry () =
    async {
        let! fixture = fixture () |> Async.AwaitTask

        try
            let memory = fixture.Store :?> MemoryStore.Store
            let actor = core fixture fixture.Owner
            let before = memory.ObservedAt

            let! _ =
                Service.context
                    (Service.withClaims (RequesterClaims.admit (Some "Hint") None None) actor)
                    ()
                    CancellationToken.None
                |> Async.AwaitTask

            memory.ObservedAt <- before.AddDays 31.
            let! _ = Service.context actor () CancellationToken.None |> Async.AwaitTask
            let request = List.exactlyOne memory.AccessRequests

            Expect.equal
                request.FirstSeen
                memory.ObservedAt
                "An expired request starts a fresh first-seen lifetime"

            Expect.isNone request.Claims.Name "A refreshed absence does not retain an old hint"
            let! _ = bootstrap fixture |> Async.AwaitTask

            Expect.isEmpty
                memory.AccessRequests
                "Owner provisioning removes its matching request atomically"
        finally
            fixture.Dispose()
    }

let private directoryBatch () =
    async {
        let! fixture = fixture () |> Async.AwaitTask

        try
            let memory = fixture.Store :?> MemoryStore.Store
            let! owner = bootstrap fixture |> Async.AwaitTask

            for number in [ 2000..2049 ] do
                let principal =
                    PrincipalKey.human "https://issuer.example/" ($"batch-{number}")
                    |> Result.defaultWith (string >> failtest)

                memory.SeedActor
                    {
                        ActorId = CaseFixtures.actorId (id number)
                        Principal = principal
                        Enabled = true
                        Profile = None
                    }
                    []

            let page limit =
                async {
                    let before = memory.Events |> List.filter ((=) "read") |> List.length

                    let! result =
                        Service.actors
                            owner
                            {
                                Role = None
                                Text = None
                                AfterCursor = None
                                Limit = limit
                            }
                            CancellationToken.None
                        |> Async.AwaitTask

                    let after = memory.Events |> List.filter ((=) "read") |> List.length

                    match result with
                    | Outcome.Succeeded page ->
                        Expect.equal page.Items.Length limit "Requested page is materialized"
                    | other -> failtestf "Directory batch: %A" other

                    return after - before
                }

            let! one = page 1
            let! many = page 50

            Expect.equal
                many
                one
                "A50-person page uses the same number of snapshot primitives as a one-person page"
        finally
            fixture.Dispose()
    }

[<Tests>]
let tests =
    testList
        "memory transaction store"
        [
            StoreContractTests.tests fixture
            testCaseAsync
                "directory role facts are one batch rather than per-person reads"
                (directoryBatch ())
            testCaseAsync
                "expired requester hints reset and provisioning deletes the request"
                (requestExpiry ())
            testCaseAsync
                "rejection discards pending writes and locking precedes reads"
                (rollback ())
        ]

[<Tests>]
let authorityFactReads =
    testTask "authority acts request only their domain-required auxiliary counts" {
        do!
            task {
                let! fixture = fixture ()

                use _owned =
                    { new IDisposable with
                        member _.Dispose() = fixture.Dispose()
                    }

                let memory = fixture.Store :?> MemoryStore.Store
                let! actor = bootstrap fixture

                let counts events =
                    events
                    |> List.filter (fun name ->
                        name = "actor-count" || name = "enabled-owner-count")

                Expect.equal
                    (counts memory.Events)
                    [ "actor-count" ]
                    "Provisioning requires only actor count"

                let target =
                    PrincipalKey.human "https://issuer.example/" "other"
                    |> Result.defaultWith (string >> failtest)

                let check operation act expected =
                    task {
                        let! result =
                            Service.execute
                                actor
                                (OperationInput.Authority(
                                    authorityRequest (id operation) target act
                                ))
                                CancellationToken.None

                        match result.Outcome with
                        | ExecuteResult.Accepted _ -> ()
                        | other ->
                            failtestf
                                "The independently authored authority act must be accepted: %A"
                                other

                        Expect.equal
                            (counts memory.Events)
                            expected
                            "Only required head-locked protection facts are queried"
                    }

                do! check 600 (AuthorityAct.Register Inputs.actorProfile) []

                do!
                    check
                        601
                        (AuthorityAct.Grant
                            {
                                Role = Role.CaseReader
                                Scope = GrantTarget.Installation
                            })
                        []

                do! check 602 AuthorityAct.Enable []

                do!
                    check
                        603
                        (AuthorityAct.Revoke
                            {
                                Role = Role.CaseReader
                                Scope = GrantTarget.Installation
                            })
                        []

                do! check 604 AuthorityAct.Disable [ "enabled-owner-count" ]

                do!
                    check
                        605
                        (AuthorityAct.Revoke
                            {
                                Role = Role.Owner
                                Scope = GrantTarget.Installation
                            })
                        [ "enabled-owner-count" ]
            }
    }
