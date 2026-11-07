module ClaimCore.IntegrationTests.ReadTimingProbe

open System
open System.Diagnostics
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private measure name read =
    task {
        let elapsed = Stopwatch.StartNew()
        let! result = read ()

        match result with
        | Outcome.Succeeded _ -> printfn "%s: %.3f ms" name elapsed.Elapsed.TotalMilliseconds
        | other -> failtestf "%s failed: %A" name other
    }

// Explicitly excluded from verification stages; run by name with CLAIMCORE_TIMING=1.
[<Tests>]
let tests =
    testTask "opt-in read timing probe" {
        if Environment.GetEnvironmentVariable "CLAIMCORE_TIMING" <> "1" then
            skiptest "Set CLAIMCORE_TIMING=1 to run this throwaway measurement."

        do!
            task {
                let! database = PostgresFixture.create ()

                use _owned =
                    { new IDisposable with
                        member _.Dispose() = database.Fixture.Dispose()
                    }

                let! actor = caseWorker database.Fixture
                let token = CancellationToken.None

                for index in 1..10000 do
                    let! reply =
                        Service.execute
                            actor
                            (Inputs.caseOperation (
                                request
                                    database.Fixture
                                    (1000000 + index)
                                    (sprintf "TIMING-%05d" index)
                                    0L
                                    (AuthoredCaseAct.Open registration)
                            ))
                            token

                    accepted reply |> ignore

                do!
                    measure "case.list" (fun () ->
                        Service.list
                            actor
                            {
                                Page = { AfterCursor = None; Limit = 50 }
                                Filter = CaseListFilter.empty
                            }
                            token)

                do!
                    measure "case.summary" (fun () ->
                        Service.caseSummary actor CaseListFilter.empty token)

                do!
                    measure "case.timeline" (fun () ->
                        Service.timeline
                            actor
                            {
                                Target = CaseSelector.Reference "TIMING-00001"
                                Filter = None
                                AfterCursor = None
                                Limit = 50
                            }
                            token)

                do! measure "work.views" (fun () -> Service.workViews actor () token)

                do!
                    measure "reminder.list" (fun () ->
                        Service.reminders
                            actor
                            {
                                View = None
                                Target = None
                                State = None
                                Due = None
                                Assignment = None
                                Text = None
                                AfterCursor = None
                                Limit = 50
                                IncludeInactive = None
                            }
                            token)

                do!
                    measure "authority.history" (fun () ->
                        Service.authorityHistory
                            actor
                            {
                                TargetActorId = None
                                AfterCursor = None
                                Limit = 50
                            }
                            token)
            }
    }
