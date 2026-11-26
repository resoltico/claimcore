module ClaimCore.IntegrationTests.SummaryAttributionPostgresTests

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected summary read: %A" other

[<Tests>]
let tests =
    testList
        "summary attribution projection"
        [
            testCaseAsync
                "summary lifecycle and erase remain causally verifiable"
                (run (fun database ->
                    task {
                        let! _ = SummaryAttributionContract.lifecycle database.Fixture
                        let! _ = verified database
                        return ()
                    }))
            testCaseAsync
                "summary attribution tamper is detected and rebuilt from the actual edit"
                (run (fun database ->
                    task {
                        let! actor = caseWorker database.Fixture

                        let! opened =
                            Service.executeSubmission
                                actor
                                (((request
                                    (database.Fixture)
                                    (88100)
                                    ("SUMMARY-REBUILD")
                                    (0L)
                                    "case.open"
                                    (ClaimCore.TestSupport.Submissions.fields (
                                        RegistrationInput.values (registration)
                                    )))))
                                token

                        let parent = accepted opened |> caseReceipt |> _.CaseId
                        let target = CaseSelector.Id parent

                        let! changed =
                            Service.executeSubmission
                                actor
                                ((ClaimCore.TestSupport.Submissions.createRaw)
                                    "work.update"
                                    (id database.Fixture 88101)
                                    (ClaimCore.Application.Contracts.OperationBinding.Work(
                                        (target),
                                        (0L)
                                    ))
                                    (ClaimCore.TestSupport.Submissions.work (
                                        {
                                            Responsible = WorkValue.Keep
                                            Summary = WorkValue.Set "Summary to reconstruct"
                                        }
                                    )))
                                token

                        let receipt = accepted changed

                        let get () =
                            Service.get actor { Target = target } token

                        let! original = get ()

                        Expect.equal
                            (read original).Work.SummaryChangedAt
                            (Some receipt.RecordedAt)
                            "Original attribution is tied to the exact edit event"

                        let! checkpoint = checkpoint database
                        do! enterMaintenance database

                        do!
                            sql
                                database
                                "UPDATE claimcore.case_work SET summary_changed_at=summary_changed_at+interval '1 second' WHERE case_id=$1"
                                [| box (CaseId.value parent) |]

                        do! finding VerificationFinding.CaseWorkProjection database

                        let! rebuilt =
                            ProjectionRebuild.rebuild
                                database.OwnerSource
                                database.MasterKey
                                checkpoint
                                (MaintenanceHandle.create
                                    1L
                                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                                 |> Option.get)
                                token

                        Expect.isOk rebuilt "Trusted ledger edit reconstructs the summary metadata"
                        let! _ = verified database
                        let! restored = get ()

                        Expect.equal
                            (read restored).Work.SummaryChangedAt
                            (Some receipt.RecordedAt)
                            "Rebuild restores the original summary edit time"

                        Expect.equal
                            ((read restored).Work.SummaryRecordedBy |> Option.map _.ActorId)
                            (Some (receipt.RecordedBy |> AuthorityRef.actor |> Option.get).ActorId)
                            "Rebuild restores the original summary author"
                    }))
        ]
