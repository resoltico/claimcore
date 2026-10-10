module ClaimCore.WitnessTests.Suite

open System.Threading
open System
open System.Security.Cryptography
open System.Threading.Tasks
open Expecto
open Npgsql
open Testcontainers.PostgreSql
open ClaimCore.Witness

open ClaimCore.WitnessTests.WitnessTestSupport

let private witnessCase1 =
    testCase "[CC-WIT-001] encrypted envelopes bind identity and reject tampering" (fun _ ->
        let key = RandomNumberGenerator.GetBytes(32)
        use cipher = new Cipher(key)
        let associated = [| 1uy; 2uy; 3uy |]
        let plain = [| 4uy; 5uy; 6uy |]
        let first = cipher.Encrypt(associated, plain)
        let second = cipher.Encrypt(associated, plain)
        Expect.notEqual first second "Random nonces produce distinct ciphertext"
        Expect.equal (cipher.Decrypt(associated, first)) plain "Exact key and identity decrypt"

        Expect.throws
            (fun () -> cipher.Decrypt([| 9uy |], first) |> ignore)
            "Different associated identity is rejected"

        first[first.Length - 1] <- first[first.Length - 1] ^^^ 1uy

        Expect.throws
            (fun () -> cipher.Decrypt(associated, first) |> ignore)
            "Ciphertext tampering is rejected")

let private witnessCase2 =
    testCase "[CC-WIT-001] rotating active key retains older decryptability" (fun _ ->
        let oldId = Guid.NewGuid()
        let newId = Guid.NewGuid()
        let oldKey = RandomNumberGenerator.GetBytes(32)
        let newKey = RandomNumberGenerator.GetBytes(32)
        use custody = new KeyRing(newId, [ oldId, oldKey; newId, newKey ]) :> IKeyCustody
        let aad = [| 1uy; 2uy |]
        let oldEvidence = custody.Encrypt(oldId, aad, payload 1uy)
        let newEvidence = custody.Encrypt(newId, aad, payload 2uy)

        Expect.equal
            (custody.Decrypt(oldId, aad, oldEvidence))
            (payload 1uy)
            "Historical key remains available"

        Expect.equal (custody.Decrypt(newId, aad, newEvidence)) (payload 2uy) "New key is active")

let private witnessCase5 =
    testCase "[CC-WIT-001] runtime role cannot mutate or administer" (fun _ ->
        fixture (fun owner writer identity capability ->
            assertRemoteTransport writer identity capability

            use store = new Store(writer, identity, capability)
            let subject = Guid.NewGuid()

            let ticket =
                (store.Append(
                    Guid.NewGuid(),
                    Some subject,
                    Intent,
                    keyId,
                    payload 5uy,
                    cancellation
                 )
                 |> await)

            Expect.equal
                (scalar<Guid>
                    owner
                    $"SELECT subject_case_id FROM claimcore_witness.journal WHERE sequence={ticket.Sequence}")
                subject
                "Immutable journal binds the case subject"

            Expect.equal
                (scalar<Guid>
                    owner
                    $"SELECT subject_case_id FROM claimcore_witness.journal_payloads WHERE sequence={ticket.Sequence}")
                subject
                "Separately prunable ciphertext binds the same subject"

            for statement in
                [
                    "UPDATE claimcore_witness.journal SET phase='SETTLED_ACCEPTED'"
                    "DELETE FROM claimcore_witness.journal"
                    "TRUNCATE claimcore_witness.journal"
                    "UPDATE claimcore_witness.journal_payloads SET encrypted_payload=decode('01','hex')"
                    "DELETE FROM claimcore_witness.journal_payloads"
                    "TRUNCATE claimcore_witness.journal_payloads"
                    "ALTER TABLE claimcore_witness.journal ADD COLUMN forged boolean"
                    "CREATE TABLE claimcore_witness.forged (id int)"
                ] do
                Expect.throws
                    (fun () -> run writer statement)
                    "Writer cannot mutate schema or history"))

let private witnessCase8 =
    testCase "[CC-WIT-001] function definition fingerprint" (fun _ ->
        fixture (fun owner _ _ _ ->
            let digest =
                scalar<string>
                    owner
                    "SELECT encode(sha256(convert_to(pg_get_functiondef('claimcore_witness.append(claimcore_witness.installation_identity,claimcore_witness.journal_subject,claimcore_witness.journal_request,bytea)'::regprocedure),'UTF8')),'hex')"

            Expect.equal
                digest
                "a1a360b7cd3f936afa55139f2b35456c90435b8b72902b7fa0aff2d43bc9bc3c"
                "Pinned PostgreSQL 18.6 deparsed function"))

let private witnessCase10 =
    testCase "[CC-WIT-001] writer cannot invoke owner-only witness functions" (fun _ ->
        fixture (fun _ writer _ _ ->
            let ownerOnlyDenied =
                scalar<bool>
                    writer
                    "SELECT count(*)=6 AND NOT bool_or(has_function_privilege(current_user,p.oid,'EXECUTE')) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='claimcore_witness' AND p.proname IN ('prepare_writer_handoff','commit_writer_handoff','abort_writer_handoff','release_aborted_writer_handoff','settle_and_prune','rotate_key')"

            Expect.isTrue
                ownerOnlyDenied
                "Writer has no owner handoff, abort, prune or rotation execute grant"))

[<Tests>]
let tests =
    testList
        "ClaimCore witness PostgreSQL"
        [
            WitnessFixtureDiagnosticsTests.tests
            witnessCase1
            witnessCase2
            WitnessAppendTests.concurrentAppend
            WitnessAppendTests.rollbackSequence
            witnessCase5
            WitnessAppendTests.durabilityAdmission
            WitnessAppendTests.settlementIdentity
            witnessCase8
            WitnessAppendTests.writerCapabilityFence
            witnessCase10
        ]
    |> testSequenced
