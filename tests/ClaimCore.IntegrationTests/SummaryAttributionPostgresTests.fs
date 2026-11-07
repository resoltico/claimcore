module ClaimCore.IntegrationTests.SummaryAttributionPostgresTests

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
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
                            Service.execute
                                actor
                                (Inputs.caseOperation (
                                    request
                                        database.Fixture
                                        88100
                                        "SUMMARY-REBUILD"
                                        0L
                                        (AuthoredCaseAct.Open registration)
                                ))
                                token

                        let parent = accepted opened |> caseReceipt |> _.CaseId
                        let target = CaseSelector.Id parent

                        let! changed =
                            Service.execute
                                actor
                                (OperationInput.Work
                                    {
                                        OperationId = id database.Fixture 88101
                                        Target = target
                                        ExpectedRevision = 0L
                                        Act =
                                            WorkAct.UpdateWork
                                                {
                                                    Responsible = WorkValue.Keep
                                                    Summary =
                                                        WorkValue.Set "Summary to reconstruct"
                                                }
                                    })
                                token

                        let receipt = accepted changed

                        let get () =
                            Service.get actor { Target = target } token

                        let! original = get ()

                        Expect.equal
                            (read original).Work.SummaryChangedAt
                            (Some receipt.RecordedAt)
                            "Original attribution is tied to the exact edit event"

                        let! checkpoint = verified database
                        do! enterMaintenance database

                        do!
                            sql
                                database
                                "UPDATE claimcore.case_work SET summary_changed_at=summary_changed_at+interval '1 second' WHERE case_id=$1"
                                [| box (CaseId.value parent) |]

                        do! finding VerificationFinding.CaseWorkProjection database

                        let! rebuilt =
                            ProjectionRebuild.rebuild database.OwnerSource checkpoint token

                        Expect.isOk rebuilt "Trusted ledger edit reconstructs the summary metadata"
                        let! _ = verified database
                        let! restored = get ()

                        Expect.equal
                            (read restored).Work.SummaryChangedAt
                            (Some receipt.RecordedAt)
                            "Rebuild restores the original summary edit time"

                        Expect.equal
                            ((read restored).Work.SummaryRecordedBy |> Option.map _.ActorId)
                            (Some receipt.RecordedBy.ActorId)
                            "Rebuild restores the original summary author"
                    }))
        ]
