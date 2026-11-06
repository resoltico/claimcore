module ClaimCore.Tests.ActorResponsibilityTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

[<Tests>]
let tests =
    testCaseAsync
        "memory responsibility counts preserve viewer scope and bounded enrichment"
        (async {
            use store =
                new MemoryStore.Store(
                    id 73500,
                    DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)
                )

            let fixture =
                {
                    Store = store :> IStore
                    InstallationId = id 73500
                    Owner =
                        PrincipalKey.human "https://issuer.example/" "initial-owner"
                        |> Result.defaultWith (string >> failtest)
                    Calendar =
                        { new IBusinessCalendar with
                            member _.DateAt instant =
                                DateOnly.FromDateTime instant.UtcDateTime

                            member _.TimeZoneId = "Etc/UTC"
                        }
                    Dispose = ignore
                }

            let corrupt actor caseId =
                task {
                    let! result =
                        fixture.Store.Read
                            {
                                Work = fun tx -> tx.FindActor actor
                                CancellationToken = CancellationToken.None
                            }

                    let account =
                        match result with
                        | Ok(Some actor) -> actor
                        | other -> failtestf "Corruption control actor: %A" other

                    store.SeedActor account [ Role.DataSteward, Some caseId ]
                }

            do! ActorResponsibilityScenarios.run fixture corrupt |> Async.AwaitTask

            for subject, expectedReads in [ "owner-only-viewer", 3; "global-viewer", 4 ] do
                let viewer =
                    PrincipalKey.human "https://issuer.example/" subject
                    |> Result.defaultWith (string >> failtest)

                let before = store.Events.Length
                let calls = store.Calls

                let! result =
                    Service.actors
                        (core fixture viewer)
                        {
                            Role = None
                            Text = None
                            AfterCursor = None
                            Limit = 50
                        }
                        CancellationToken.None
                    |> Async.AwaitTask

                match result with
                | Outcome.Succeeded _ -> ()
                | other -> failtestf "Directory budget read: %A" other

                Expect.equal
                    (store.Calls - calls)
                    1
                    "Authority, directory and enrichment share one read transaction"

                Expect.equal
                    (store.Events.Length - before)
                    expectedReads
                    "Directory roles use one bounded batch; responsibility adds one only when the viewer can read cases"
        })
