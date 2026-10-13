module ClaimCore.IntegrationTests.CaseReadProofCorruptionTests

open System
open System.IO
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures

let private mixedPage () =
    let receipts = CaseReadProofBatchTests.receipts 2
    let witness = witnessProtocol ()
    use owner = new NpgsqlConnection(adminConnection ())
    owner.Open()
    use transaction = owner.BeginTransaction()

    use primary =
        new NpgsqlCommand(
            "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR SHARE",
            owner,
            transaction
        )

    primary.ExecuteScalar() |> ignore
    use _fence = witness.AcquireReadFence(1L, CancellationToken.None) |> await

    use corrupt =
        new NpgsqlCommand(
            "UPDATE claimcore.case_changes SET snapshot=(SELECT snapshot FROM claimcore.case_changes WHERE operation_id=@other) WHERE operation_id=@operation",
            owner,
            transaction
        )

    corrupt.Parameters.AddWithValue("other", receipts.Head.OperationId) |> ignore
    corrupt.Parameters.AddWithValue("operation", receipts[1].OperationId) |> ignore

    Expect.equal
        (corrupt.ExecuteNonQuery())
        1
        "Only one synthetic subject is altered in an uncommitted transaction."

    Expect.throwsT<InvalidDataException>
        (fun () ->
            CaseReadEvidence.page
                witness
                owner
                transaction
                (receipts |> List.map _.Case)
                CancellationToken.None
            |> await
            |> ignore)
        "A valid first row cannot disclose a partial page containing a cross-case snapshot."

    transaction.Rollback()

let tests =
    testCase "[CC-AUTH-001] one corrupt subject refuses an otherwise valid current page" mixedPage
