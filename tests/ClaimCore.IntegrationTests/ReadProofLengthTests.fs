module ClaimCore.IntegrationTests.ReadProofLengthTests

open System
open System.IO
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.Fixtures

let private encodedPrimaryBound () =
    use database = store ()

    let receipt =
        CommandExecution.executeAsync (database :> IClaimStore) clock (newRequest ())
        |> await
        |> accepted

    use owner = new NpgsqlConnection(adminConnection ())
    owner.Open()
    use transaction = owner.BeginTransaction()

    use admissionConstraint =
        new NpgsqlCommand(
            "ALTER TABLE claimcore.case_changes DROP CONSTRAINT case_changes_canonical_request_check",
            owner,
            transaction
        )

    admissionConstraint.ExecuteNonQuery() |> ignore

    let update length =
        use command =
            new NpgsqlCommand(
                "UPDATE claimcore.case_changes SET canonical_request=decode(repeat('aa',@length),'hex') WHERE operation_id=@operation",
                owner,
                transaction
            )

        command.Parameters.AddWithValue("length", length) |> ignore
        command.Parameters.AddWithValue("operation", receipt.OperationId) |> ignore
        command.ExecuteNonQuery() |> ignore

    update 65536

    let admitted =
        CaseReadProof.loadOperations
            owner
            transaction
            [| receipt.OperationId |]
            CancellationToken.None
        |> await

    Expect.equal
        admitted[receipt.OperationId].Request.Length
        65536
        "Encoded transport maximum is not arbitrarily narrowed; this alone is not canonical verification."

    update (8 * 1024 * 1024)
    let allocated = GC.GetTotalAllocatedBytes(true)

    Expect.throwsT<InvalidDataException>
        (fun () ->
            CaseReadProof.loadOperations
                owner
                transaction
                [| receipt.OperationId |]
                CancellationToken.None
            |> await
            |> ignore)
        "Oversized evidence is omitted by SQL before driver materialization."

    Expect.isLessThan
        (GC.GetTotalAllocatedBytes(true) - allocated)
        (1024L * 1024L)
        "An eight-MiB corrupt input cannot cause an eight-MiB allocation."

    transaction.Rollback()

let tests =
    testCase
        "[CC-AUTH-001] primary proof encoded lengths are admitted before allocation"
        encodedPrimaryBound
