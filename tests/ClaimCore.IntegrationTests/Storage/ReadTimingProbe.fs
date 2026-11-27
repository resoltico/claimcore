module ClaimCore.IntegrationTests.ReadTimingProbe

open System
open System.Diagnostics
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
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

                for index in 1..128 do
                    let! reply =
                        Service.executeSubmission
                            actor
                            (((request
                                (database.Fixture)
                                ((1000000 + index))
                                ((sprintf "TIMING-%05d" index))
                                (0L)
                                "case.open"
                                (ClaimCore.TestSupport.Submissions.fields (
                                    RegistrationInput.values (registration)
                                )))))
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
                        Service.caseSummary
                            actor
                            (CaseSummaryRequest.ungrouped CaseListFilter.empty)
                            token)

                do!
                    measure "case.timeline" (fun () ->
                        Service.timeline
                            actor
                            {
                                Target = caseTarget database.Fixture "TIMING-00001"
                                Filter = None
                                AfterCursor = None
                                Limit = 50
                            }
                            token)

                // Twenty foreground refreshes over a three-page register exercise repeated audit
                // writes while the visible contexts and counts still have independent reads.
                let samples = ResizeArray<float>()

                for _ in 1..20 do
                    let elapsed = Stopwatch.StartNew()
                    do! measure "context" (fun () -> Service.context actor () token)
                    do! measure "work.views" (fun () -> Service.workViews actor () token)

                    do!
                        measure "case.list" (fun () ->
                            Service.list
                                actor
                                {
                                    Page = { AfterCursor = None; Limit = 50 }
                                    Filter = CaseListFilter.empty
                                }
                                token)

                    samples.Add elapsed.Elapsed.TotalMilliseconds

                let ordered = samples |> Seq.sort |> Seq.toArray

                printfn
                    "Foreground refresh, 128 synthetic cases: median %.3f ms, p95 %.3f ms"
                    ordered[9]
                    ordered[18]


                do!
                    measure "reminder.list" (fun () ->
                        Service.reminders
                            actor
                            {
                                View = None
                                Target = None
                                State = None
                                Kind = None
                                DeadlineOutcome = None
                                Due = None
                                Assignment = None
                                Text = None
                                AfterCursor = None
                                Limit = 50
                                IncludeInactive = None
                            }
                            token)

                do!
                    measure "people.history" (fun () ->
                        Service.peopleHistory
                            actor
                            {
                                TargetActorId = None
                                AfterCursor = None
                                Limit = 50
                            }
                            token)
            }
    }
