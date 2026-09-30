module ClaimCore.WitnessTests.Suite

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

let private witnessCase3 =
    testCase "[CC-WIT-001] gapless concurrent append and exact retry" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            let operation = Guid.NewGuid()
            let first = store.Append(operation, None, Intent, keyId, payload 1uy)
            let retry = store.Append(operation, None, Intent, keyId, payload 1uy)
            Expect.equal retry.Sequence first.Sequence "Exact retry returns original ticket"
            Expect.equal retry.EntryHash first.EntryHash "Exact retry keeps original hash"

            Expect.throws
                (fun () -> store.Append(operation, None, Intent, keyId, payload 2uy) |> ignore)
                "Divergent retry must fail"

            let tickets =
                [| 1..12 |]
                |> Array.map (fun i ->
                    Task.Run(fun () ->
                        use concurrentStore = new Store(writer, identity, capability)

                        concurrentStore.Append(
                            Guid.NewGuid(),
                            None,
                            Intent,
                            keyId,
                            payload (byte i)
                        )))

            Task.WaitAll(tickets |> Array.map (fun task -> task :> Task))

            let sequences =
                tickets |> Array.map (fun task -> task.Result.Sequence) |> Array.sort

            Expect.sequenceEqual sequences [| 2L .. 13L |] "Transactional tip has no gaps"

            Expect.equal
                (scalar<int64> owner "SELECT tip_sequence FROM claimcore_witness.installation")
                13L
                "Tip follows rows"))

let private witnessCase4 =
    testCase "[CC-WIT-001] rollback does not consume sequence" (fun _ ->
        fixture (fun owner writer identity capability ->
            use db = new NpgsqlConnection(writer)
            db.Open()
            use tx = db.BeginTransaction()

            use command =
                new NpgsqlCommand(
                    "SELECT sequence FROM claimcore_witness.append(@i,@l,@e,@o,'INSTALLATION',NULL,'INTENT',@k,@p,@cap)",
                    db,
                    tx
                )

            command.Parameters.AddWithValue("i", identity.InstallationId) |> ignore
            command.Parameters.AddWithValue("l", identity.LineageId) |> ignore
            command.Parameters.AddWithValue("e", identity.Epoch) |> ignore
            command.Parameters.AddWithValue("o", Guid.NewGuid()) |> ignore
            command.Parameters.AddWithValue("k", keyId) |> ignore
            command.Parameters.AddWithValue("p", payload 3uy) |> ignore
            command.Parameters.AddWithValue("cap", capability) |> ignore

            Expect.equal
                (command.ExecuteScalar() :?> int64)
                1L
                "Uncommitted row reserves first sequence"

            tx.Rollback()

            use committedStore = new Store(writer, identity, capability)

            Expect.equal
                (committedStore.Append(Guid.NewGuid(), None, Intent, keyId, payload 4uy).Sequence)
                1L
                "Rollback reuses first sequence"

            Expect.equal
                (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                1L
                "Only committed row remains"))

let private witnessCase5 =
    testCase "[CC-WIT-001] runtime role cannot mutate or administer" (fun _ ->
        fixture (fun owner writer identity capability ->
            assertRemoteTransport writer identity capability

            use store = new Store(writer, identity, capability)
            let subject = Guid.NewGuid()
            let ticket = store.Append(Guid.NewGuid(), Some subject, Intent, keyId, payload 5uy)

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

let private witnessCase6 =
    testCase "[CC-WIT-001] unsafe durability fails before append" (fun _ ->
        fixture (fun owner writer identity capability ->
            let builder = NpgsqlConnectionStringBuilder(writer)
            builder.Options <- "-c synchronous_commit=off"

            Expect.throws
                (fun () ->
                    use unsafeStore = new Store(builder.ConnectionString, identity, capability)

                    unsafeStore.Append(Guid.NewGuid(), None, Intent, keyId, payload 6uy)
                    |> ignore)
                "Session durability admission fails"

            Expect.equal
                (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                0L
                "No append happened"))

let private witnessCase7 =
    testCase "[CC-WIT-001] settlement requires exact unsettled intent" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            let operation = Guid.NewGuid()

            Expect.throws
                (fun () ->
                    store.Append(operation, None, SettledAccepted, keyId, payload 7uy) |> ignore)
                "Settlement without intent is rejected"

            let intent = store.Append(operation, None, Intent, keyId, payload 7uy)
            let settlement = store.Append(operation, None, SettledAccepted, keyId, payload 8uy)
            Expect.equal settlement.Sequence (intent.Sequence + 1L) "Settlement follows intent"

            Expect.throws
                (fun () ->
                    store.Append(operation, None, SettledRevoked, keyId, payload 9uy) |> ignore)
                "Contradictory settlement is rejected"

            Expect.equal
                (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                2L
                "Only one settlement persisted"))

let private witnessCase8 =
    testCase "[CC-WIT-001] function definition fingerprint" (fun _ ->
        fixture (fun owner _ _ _ ->
            let digest =
                scalar<string>
                    owner
                    "SELECT encode(sha256(convert_to(pg_get_functiondef('claimcore_witness.append(uuid,uuid,bigint,uuid,text,uuid,text,uuid,bytea,bytea)'::regprocedure),'UTF8')),'hex')"

            Expect.equal
                digest
                "770a563cf040e29ba8868063c85df19829452b10af67dd2b908c3a2b15c72d02"
                "Pinned PostgreSQL 18.6 deparsed function"))

let private witnessCase9 =
    testCase "[CC-WIT-001] writer capability and pending handoff fence every append" (fun _ ->
        fixture (fun owner writer identity capability ->
            let wrongCapability = RandomNumberGenerator.GetBytes(32)

            try
                use wrongStore = new Store(writer, identity, wrongCapability)

                Expect.throws
                    (fun () ->
                        wrongStore.Append(Guid.NewGuid(), None, Intent, keyId, payload 1uy)
                        |> ignore)
                    "A different capability cannot append"

                Expect.equal
                    (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                    0L
                    "Rejected capability leaves no row"

                run
                    owner
                    "UPDATE claimcore_witness.installation SET handoff_pending=true WHERE singleton"

                use currentStore = new Store(writer, identity, capability)

                Expect.throws
                    (fun () ->
                        currentStore.Append(Guid.NewGuid(), None, Intent, keyId, payload 2uy)
                        |> ignore)
                    "A pending handoff fences even the current writer"

                Expect.equal
                    (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                    0L
                    "Pending fence leaves no row"
            finally
                CryptographicOperations.ZeroMemory(wrongCapability)))

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
            witnessCase1
            witnessCase2
            witnessCase3
            witnessCase4
            witnessCase5
            witnessCase6
            witnessCase7
            witnessCase8
            witnessCase9
            witnessCase10
        ]
    |> testSequenced
