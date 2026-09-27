module ClaimCore.IntegrationTests.ManagedCopyWitnessTipTests

open ClaimCore.Postgres.WitnessProtocolReconciliation

open System
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.FixtureWitnessWriterStore

let private assertBadHashes (witness: WitnessProtocol) tip settled =
    witness.VerifyHistoricalTip(tip.TipSequence, tip.TipHash)
    witness.VerifyHistoricalTip(settled.TipSequence, settled.TipHash)
    witness.VerifyHistoricalTip(0L, Array.zeroCreate<byte> 32)
    let wrongHash = Array.copy settled.TipHash
    wrongHash[0] <- wrongHash[0] ^^^ 1uy

    Expect.throws
        (fun () -> witness.VerifyHistoricalTip(settled.TipSequence, wrongHash))
        "Changed historical hash is refused."

    Expect.throws
        (fun () -> witness.VerifyHistoricalTip(settled.TipSequence + 1L, settled.TipHash))
        "Missing sequence is refused."

let private assertWrongEpoch writer (witness: WitnessProtocol) settled =
    let wrongIdentity =
        { witness.Identity with
            Epoch = witness.Identity.Epoch + 1L
        }

    let wrongStore = FixtureWitnessWriterStore.current writer wrongIdentity
    let keyId = witness.KeyCustody.ActiveKeyId

    let custody = new KeyRing(keyId, [ keyId, witnessKey () ]) :> IKeyCustody

    use wrongEpoch = new WitnessProtocol(wrongStore, custody, wrongIdentity)

    Expect.throws
        (fun () -> wrongEpoch.VerifyHistoricalTip(settled.TipSequence, settled.TipHash))
        "Wrong witness epoch is refused."

let private truncateSyntheticTip (writer: string) (witness: WitnessProtocol) (settled: Snapshot) =
    let ownerBuilder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    ownerBuilder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    use owner = new NpgsqlConnection(ownerBuilder.ConnectionString)
    owner.Open()

    use current =
        new NpgsqlCommand(
            "SELECT count(*),COALESCE(max(sequence),0) FROM claimcore_witness.journal",
            owner
        )

    use currentRows = current.ExecuteReader()
    Expect.isTrue (currentRows.Read()) "Synthetic witness table is readable."
    let countBefore = currentRows.GetInt64(0)
    let highest = currentRows.GetInt64(1)
    currentRows.Close()
    Expect.isGreaterThan countBefore 0L "Witness must contain synthetic entries."
    Expect.equal highest settled.TipSequence "Current synthetic tip sequence is exact."

    use deletePayload =
        new NpgsqlCommand(
            "DELETE FROM claimcore_witness.journal_payloads WHERE sequence=@sequence",
            owner
        )

    deletePayload.Parameters.AddWithValue("sequence", settled.TipSequence) |> ignore
    Expect.equal (deletePayload.ExecuteNonQuery()) 1 "Only disposable ciphertext is removed."

    use truncate =
        new NpgsqlCommand("DELETE FROM claimcore_witness.journal WHERE sequence=@sequence", owner)

    truncate.Parameters.AddWithValue("sequence", settled.TipSequence) |> ignore
    Expect.equal (truncate.ExecuteNonQuery()) 1 "Only disposable synthetic tip is removed."

    Expect.throws
        (fun () -> witness.VerifyHistoricalTip(settled.TipSequence, settled.TipHash))
        "Truncated witness history is refused."

let private historicalTip =
    testCase
        "[CC-BACKUP-001] historical witness tip rejects wrong hash, epoch and truncation"
        (fun _ ->
            withAuthorityRuntimeDatabase (fun _ _ writer witness ->
                let tip = witness.Snapshot()
                let operation = Guid.NewGuid()
                let canonical = [| 0x43uy; 0x43uy; 0x54uy |]
                let intent = witness.BeginAuthority(operation, canonical, None)
                witness.SettleAuthority(operation, intent) |> ignore
                let settled = witness.Snapshot()

                assertBadHashes witness tip settled
                assertWrongEpoch writer witness settled
                truncateSyntheticTip writer witness settled))

let tests = testList "managed-copy historical witness" [ historicalTip ]
