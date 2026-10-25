module ClaimCore.Tests.ReceiptCorrelationTests

open System
open Expecto
open ClaimCore.Contract

let private id = Guid.Parse "10000000-0000-4000-8000-000000000001"
let private other = Guid.Parse "10000000-0000-4000-8000-000000000002"

let private request =
    OperationInput.Case
        {
            OperationId = id
            CaseReference = "SYNTHETIC-1"
            ExpectedRevision = 2L
            Act = CaseAct.Close
        }

let private caseReceipt =
    {
        Reference = "synthetic-1"
        Revision = 3L
        Act = CaseActKind.Business BusinessActKind.Close
        Snapshot = SnapshotDisclosure.Withheld
    }

let private receipt scope =
    {
        OperationId = id
        Position =
            {
                Sequence = 1L
                EntryHash = String.replicate 64 "a"
            }
        RecordedAt = DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)
        RecordedBy = other
        Scope = scope
    }

let private matches value =
    let endpoint = Api.tryFind "operation.execute" |> Option.get

    Correlation.matches
        endpoint
        (Api.operationExecute.Input.Write request)
        (Api.operationExecute.Output.Write
            {

                OperationId = id
                Outcome = ExecuteResult.Accepted value
            })

let private contradictions () =
    let original = receipt (ReceiptScope.Case caseReceipt)
    Expect.isTrue (matches original) "ASCII case identity and successor revision correlate."

    let invalid =
        [
            { original with OperationId = other }
            receipt (
                ReceiptScope.Authority
                    {
                        TargetActorId = other
                        Act = AuthorityKind.SetEnabled
                    }
            )
            receipt (
                ReceiptScope.Case
                    { caseReceipt with
                        Reference = "SYNTHETIC-2"
                    }
            )
            receipt (ReceiptScope.Case { caseReceipt with Revision = 2L })
            receipt (
                ReceiptScope.Case
                    { caseReceipt with
                        Act = CaseActKind.Business BusinessActKind.Reopen
                    }
            )
        ]

    for value in invalid do
        Expect.isFalse
            (matches value)
            "A contradictory accepted receipt cannot settle the prepared request."

let private observation () =
    let endpoint = Api.tryFind "operation.observe" |> Option.get
    let input = Api.operationObserve.Input.Write { OperationId = id }

    for actual, expected in [ id, true; other, false ] do
        let value =
            { receipt (ReceiptScope.Case caseReceipt) with
                OperationId = actual
            }

        let output = Api.operationObserve.Output.Write(ReadReply.Succeeded value)

        Expect.equal
            (Correlation.matches endpoint input output)
            expected
            "Observation echoes the requested operation."

[<Tests>]
let tests =
    testList
        "typed receipt correlation"
        [
            testCase "receipt contradictions reject" contradictions
            testCase "observations echo operation identity" observation
        ]
