module ClaimCore.Tests.MemoryStoreTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private fixture () =
    let store =
        new MemoryStore.Store(DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero))

    Task.FromResult
        {
            Store = store :> IStore
            InstallationId = id 500
            Owner =
                PrincipalKey.human "https://issuer.example/" "owner"
                |> Result.defaultWith failtest
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
        use memory = new MemoryStore.Store(DateTimeOffset.UtcNow)
        let store = memory :> IStore

        let principal =
            PrincipalKey.human "https://issuer.example/" "rollback"
            |> Result.defaultWith failtest

        for verdict in
            [
                Verdict.Refuse Refusal.ResourceUnavailable
                Verdict.Fail ClaimCore.Contract.CoreFault.InternalError
            ] do
            let! result =
                store.Write(
                    (fun tx ->
                        task {
                            let! _ = tx.ActorCount()

                            do!
                                tx.ProjectAuthority(
                                    AuthorityProjection.Register(id 800, principal)
                                )

                            return verdict
                        }),
                    CancellationToken.None
                )
                |> Async.AwaitTask

            Expect.equal result (Ok verdict) "Verdict survives rollback"

            Expect.equal
                (List.head memory.Events)
                "lock"
                "Write capability is created after locking"

            let! count =
                store.Read((fun tx -> tx.ActorCount()), CancellationToken.None)
                |> Async.AwaitTask

            Expect.equal count (Ok 0) "Rejected staged projection is discarded"
    }

[<Tests>]
let tests =
    testList
        "memory transaction store"
        [
            StoreContractTests.tests fixture
            testCaseAsync
                "rejection discards pending writes and locking precedes reads"
                (rollback ())
        ]
