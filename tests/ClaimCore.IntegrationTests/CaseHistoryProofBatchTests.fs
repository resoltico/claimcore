module ClaimCore.IntegrationTests.CaseHistoryProofBatchTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ReadProofMeasurements

let private historyReceipts count =
    use database = store ()
    let service = database :> IClaimStore
    let reference = "history-proof-" + Guid.NewGuid().ToString("N")

    [
        for index in 0 .. count - 1 ->
            let request =
                { newRequest () with
                    CaseReference = reference
                    ExpectedVersion = int64 index
                    Command =
                        if index = 0 then (newRequest ()).Command
                        elif index % 2 = 1 then Command.Close
                        else Command.Reopen
                }

            CommandExecution.executeAsync service clock request |> await |> accepted
    ]

let private proof count =
    testCase ($"[CC-AUTH-001] history batches {count} exact operations for one case") (fun () ->
        let receipts = historyReceipts count
        let witness = witnessProtocol ()

        withFence (appConnection ()) witness (fun connection transaction ->
            task {
                let! rows =
                    CaseReadProof.loadOperations
                        connection
                        transaction
                        [| receipts.Head.OperationId |]
                        CancellationToken.None

                let caseId = rows[receipts.Head.OperationId].CaseId

                let! failure, commands =
                    measure "history" count (fun () ->
                        CaseReadEvidence.history
                            witness
                            connection
                            transaction
                            caseId
                            receipts
                            CancellationToken.None)

                Expect.isNone
                    failure
                    "Every historic operation retains its independent exact proof."

                Expect.isLessThanOrEqual
                    commands
                    (((count + 7) / 8) * 8)
                    "Chunks count operations, not distinct case IDs."
            })
        |> await)

let tests = [ 1; 8; 9; 50 ] |> List.map proof |> testList "history proof units"
