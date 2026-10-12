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

let private encodedWitnessBound () =
    let receipt = CaseReadProofBatchTests.receipts 1 |> List.head
    let witness = witnessProtocol ()
    use owner = new NpgsqlConnection(witnessOwnerConnection ())
    owner.Open()
    use transaction = owner.BeginTransaction()

    use admissionConstraint =
        new NpgsqlCommand(
            "ALTER TABLE claimcore_witness.journal_payloads DROP CONSTRAINT journal_payloads_encrypted_payload_check",
            owner,
            transaction
        )

    admissionConstraint.ExecuteNonQuery() |> ignore

    let update length =
        use command =
            new NpgsqlCommand(
                "UPDATE claimcore_witness.journal_payloads p SET encrypted_payload=decode(repeat('aa',@length),'hex') FROM claimcore_witness.journal j WHERE p.installation_id=j.installation_id AND p.sequence=j.sequence AND j.operation_id=@operation AND j.phase='INTENT'",
                owner,
                transaction
            )

        command.Parameters.AddWithValue("length", length) |> ignore
        command.Parameters.AddWithValue("operation", receipt.OperationId) |> ignore
        Expect.equal (command.ExecuteNonQuery()) 1 "Only the synthetic intent is altered."

    let read () =
        ClaimCore.Witness.WitnessEvidenceBatch.read
            witness.Identity
            owner
            [ receipt.OperationId, ClaimCore.Witness.SettledAccepted ]
            CancellationToken.None
        |> await

    update (1024 * 1024)

    use digest =
        new NpgsqlCommand(
            "UPDATE claimcore_witness.journal j SET payload_sha256=sha256(p.encrypted_payload) FROM claimcore_witness.journal_payloads p WHERE p.installation_id=j.installation_id AND p.sequence=j.sequence AND j.operation_id=@operation AND j.phase='INTENT'",
            owner,
            transaction
        )

    digest.Parameters.AddWithValue("operation", receipt.OperationId) |> ignore
    digest.ExecuteNonQuery() |> ignore

    let admitted = read ()

    Expect.equal
        admitted[receipt.OperationId, ClaimCore.Witness.Intent].EncryptedPayload.Length
        (1024 * 1024)
        "Encoded envelope maximum is retained; length admission alone does not verify ciphertext."

    update (8 * 1024 * 1024)
    let allocated = GC.GetTotalAllocatedBytes(true)

    Expect.throwsT<InvalidOperationException>
        (fun () -> read () |> ignore)
        "SQL omits corrupt oversized ciphertext before the driver materializes it."

    Expect.isLessThan
        (GC.GetTotalAllocatedBytes(true) - allocated)
        (1024L * 1024L)
        "An oversized witness input cannot force its encoded allocation."

    transaction.Rollback()

let tests =
    testList
        "proof encoded bounds"
        [
            testCase
                "[CC-AUTH-001] primary proof encoded lengths are admitted before allocation"
                encodedPrimaryBound
            testCase
                "[CC-AUTH-001] witness proof encoded lengths are admitted before allocation"
                encodedWitnessBound
        ]
