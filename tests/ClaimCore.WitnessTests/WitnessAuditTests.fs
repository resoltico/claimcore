module ClaimCore.WitnessTests.WitnessAuditTests

open System
open System.Security.Cryptography
open System.Threading.Tasks
open Expecto
open Npgsql
open Testcontainers.PostgreSql
open ClaimCore.Witness
open ClaimCore.WitnessTests.WitnessTestSupport
open ClaimCore.WitnessTests.WitnessAuditContinuationTests

let private witnessCase9 =
    testCase "[CC-WIT-001] catalog fingerprint" (fun _ ->
        fixture (fun owner _ _ _ ->
            let digest = scalar<string> owner (Baseline.catalogScript ())

            Expect.equal
                digest
                (Baseline.catalogDigest ())
                "Pinned PostgreSQL 18.6 witness catalog"))

let private witnessCase10 =
    testCase "[CC-WIT-001] altered function or ACL closes admission" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            store.Admit()

            run
                owner
                "REVOKE EXECUTE ON FUNCTION claimcore_witness.append(uuid,uuid,bigint,uuid,text,uuid,text,uuid,bytea,bytea) FROM claimcore_witness_writer"

            Expect.throws (fun () -> store.Admit()) "Revoked append privilege is rejected"

            run
                owner
                "GRANT EXECUTE ON FUNCTION claimcore_witness.append(uuid,uuid,bigint,uuid,text,uuid,text,uuid,bytea,bytea) TO claimcore_witness_writer"

            store.Admit()

            run
                owner
                "ALTER FUNCTION claimcore_witness.append(uuid,uuid,bigint,uuid,text,uuid,text,uuid,bytea,bytea) SET search_path = public"

            Expect.throws (fun () -> store.Admit()) "Changed trusted path is rejected"))

let private witnessCase11 =
    testCase "[CC-WIT-001] stale epoch and altered tip close admission" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            store.Append(Guid.NewGuid(), None, Intent, keyId, payload 10uy) |> ignore
            run owner "UPDATE claimcore_witness.installation SET epoch=epoch+1"
            Expect.throws (fun () -> store.Admit()) "Old writer epoch is rejected"

            run
                owner
                "UPDATE claimcore_witness.installation SET epoch=epoch-1, tip_hash=decode(repeat('ff',32),'hex')"

            Expect.throws (fun () -> store.Admit()) "Tampered tip is rejected"))

let private witnessCase12 =
    testCase "[CC-WIT-001] missing encrypted evidence fails readback" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            let operation = Guid.NewGuid()
            store.Append(operation, None, Intent, keyId, payload 11uy) |> ignore

            run
                owner
                "UPDATE claimcore_witness.journal_payloads SET encrypted_payload=decode('01','hex')"

            Expect.throws
                (fun () -> store.Read(operation, Intent) |> ignore)
                "Corrupted opaque payload is rejected"

            run owner "DELETE FROM claimcore_witness.journal_payloads"

            Expect.throws
                (fun () -> store.Read(operation, Intent) |> ignore)
                "Missing opaque payload is not treated as no journal row"

            Expect.throws
                (fun () -> store.TryReadTipEvidence() |> ignore)
                "Missing tip payload cannot be treated as an empty journal"))

let private witnessCase13 =
    testCase "[CC-WIT-001] weaker same-name constraint closes admission" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            store.Admit()

            run
                owner
                "ALTER TABLE claimcore_witness.journal DROP CONSTRAINT journal_phase_check; ALTER TABLE claimcore_witness.journal ADD CONSTRAINT journal_phase_check CHECK (true);"

            Expect.throws
                (fun () -> store.Admit())
                "Catalog fingerprint detects weaker expression"))

let private witnessCase14 =
    testCase "[CC-WIT-001] owner rotation advances journal and fences old key" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            let first = store.Append(Guid.NewGuid(), None, Intent, keyId, payload 1uy)
            let newId = Guid.NewGuid()
            let newKey = RandomNumberGenerator.GetBytes(32)
            use custody = new KeyRing(newId, [ newId, newKey ]) :> IKeyCustody
            let keyCheck = KeyCheck.create custody identity.InstallationId identity.LineageId
            let rotation = Guid.NewGuid()

            let encrypted =
                KeyCheck.rotationEnvelope
                    custody
                    identity.InstallationId
                    identity.LineageId
                    identity.Epoch
                    rotation
                    keyId
                    newId

            let ticket =
                KeyRotation.rotateKey
                    owner
                    identity
                    rotation
                    keyId
                    newId
                    keyCheck
                    encrypted
                    capability

            Expect.equal ticket.Sequence (first.Sequence + 1L) "Rotation is journaled"

            let evidence =
                store.TryReadEvidence(rotation, KeyRotated)
                |> Option.defaultWith (fun () -> failtest "Rotation evidence must exist.")

            KeyCheck.verifyRotation
                custody
                identity.InstallationId
                identity.LineageId
                identity.Epoch
                rotation
                keyId
                newId
                evidence.EncryptedPayload

            Expect.equal (fst (store.ReadKeyCheck())) newId "New key marker is active"

            Expect.throws
                (fun () ->
                    store.Append(Guid.NewGuid(), None, Intent, keyId, payload 4uy) |> ignore)
                "Stale writer key is fenced"

            let after = store.Append(Guid.NewGuid(), None, Intent, newId, payload 5uy)
            Expect.equal after.Sequence (ticket.Sequence + 1L) "New key appends after rotation"))

let private witnessCase15 =
    testCase "[CC-WIT-001] paged scan verifies contiguous hash chain" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            let first = store.Append(Guid.NewGuid(), None, Intent, keyId, payload 1uy)
            let second = store.Append(Guid.NewGuid(), None, Intent, keyId, payload 2uy)
            let tip = store.Snapshot()
            Expect.equal tip.TipSequence second.Sequence "Snapshot captures committed cutoff"
            let firstPage = store.ReadPage(0L, Array.zeroCreate<byte> 32, tip.TipSequence, 1)
            Expect.equal firstPage.Items.Length 1 "First page is bounded"
            Expect.equal firstPage.NextAfter (Some first.Sequence) "Cursor advances by sequence"
            let lastPage = store.ReadPage(first.Sequence, first.EntryHash, tip.TipSequence, 1)
            Expect.equal lastPage.Items.Length 1 "Second page completes cutoff"
            Expect.equal lastPage.NextAfter None "Cutoff is complete"

            run
                owner
                $"DELETE FROM claimcore_witness.journal_payloads WHERE sequence={first.Sequence}"

            run owner $"DELETE FROM claimcore_witness.journal WHERE sequence={first.Sequence}"

            Expect.throws
                (fun () ->
                    store.ReadPage(0L, Array.zeroCreate<byte> 32, tip.TipSequence, 2) |> ignore)
                "Missing first row is not hidden by surviving tip"))

let private witnessCase16 =
    testCase "[CC-WIT-001] authority settlement binds one exact intent" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            let operation = Guid.NewGuid()
            let first = store.Append(operation, None, Intent, keyId, payload 12uy)
            let settled = store.Append(operation, None, SettledAuthority, keyId, payload 13uy)

            Expect.equal
                settled.Sequence
                (first.Sequence + 1L)
                "Authority settlement consumes one global sequence"

            Expect.equal
                (store.Append(operation, None, SettledAuthority, keyId, payload 13uy).Sequence)
                settled.Sequence
                "Exact settlement retry returns its original ticket"

            Expect.throws
                (fun () ->
                    store.Append(operation, None, SettledAccepted, keyId, payload 14uy) |> ignore)
                "Contradictory settlement is refused"

            Expect.equal
                (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                2L
                "No duplicate authority effect"))

[<Tests>]
let tests =
    testList
        "ClaimCore witness PostgreSQL"
        [
            witnessCase9
            witnessCase10
            witnessCase11
            witnessCase12
            witnessCase13
            witnessCase14
            witnessCase15
            witnessCase16
            yield! continuationCases
        ]
    |> testSequenced
