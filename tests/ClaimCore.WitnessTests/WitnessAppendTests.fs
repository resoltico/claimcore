module ClaimCore.WitnessTests.WitnessAppendTests

open System
open System.Threading.Tasks
open System.Security.Cryptography
open Expecto
open Npgsql
open ClaimCore.Witness
open ClaimCore.WitnessTests.WitnessTestSupport

let concurrentAppend =
    testCase "[CC-WIT-001] gapless concurrent append and exact retry" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            let operation = Guid.NewGuid()

            let first =
                (store.Append(operation, None, Intent, keyId, payload 1uy, cancellation) |> await)

            let retry =
                (store.Append(operation, None, Intent, keyId, payload 1uy, cancellation) |> await)

            Expect.equal retry.Sequence first.Sequence "Exact retry returns original ticket"
            Expect.equal retry.EntryHash first.EntryHash "Exact retry keeps original hash"

            Expect.throws
                (fun () ->
                    (store.Append(operation, None, Intent, keyId, payload 2uy, cancellation)
                     |> await)
                    |> ignore)
                "Divergent retry must fail"

            let tickets =
                [| 1..12 |]
                |> Array.map (fun i ->
                    Task.Run(fun () ->
                        use concurrentStore = new Store(writer, identity, capability)

                        (concurrentStore.Append(
                            Guid.NewGuid(),
                            None,
                            Intent,
                            keyId,
                            payload (byte i),
                            cancellation
                         )
                         |> await)))

            Task.WaitAll(tickets |> Array.map (fun task -> task :> Task))

            let sequences =
                tickets |> Array.map (fun task -> task.Result.Sequence) |> Array.sort

            Expect.sequenceEqual sequences [| 2L .. 13L |] "Transactional tip has no gaps"

            Expect.equal
                (scalar<int64> owner "SELECT tip_sequence FROM claimcore_witness.installation")
                13L
                "Tip follows rows"))


let rollbackSequence =
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
                ((committedStore.Append(
                    Guid.NewGuid(),
                    None,
                    Intent,
                    keyId,
                    payload 4uy,
                    cancellation
                  )
                  |> await)
                    .Sequence)
                1L
                "Rollback reuses first sequence"

            Expect.equal
                (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                1L
                "Only committed row remains"))


let durabilityAdmission =
    testCase "[CC-WIT-001] unsafe durability fails before append" (fun _ ->
        fixture (fun owner writer identity capability ->
            let builder = NpgsqlConnectionStringBuilder(writer)
            builder.Options <- "-c synchronous_commit=off"

            Expect.throws
                (fun () ->
                    use unsafeStore = new Store(builder.ConnectionString, identity, capability)

                    (unsafeStore.Append(
                        Guid.NewGuid(),
                        None,
                        Intent,
                        keyId,
                        payload 6uy,
                        cancellation
                     )
                     |> await)
                    |> ignore)
                "Session durability admission fails"

            Expect.equal
                (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                0L
                "No append happened"))


let settlementIdentity =
    testCase "[CC-WIT-001] settlement requires exact unsettled intent" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            let operation = Guid.NewGuid()

            Expect.throws
                (fun () ->
                    (store.Append(
                        operation,
                        None,
                        SettledAccepted,
                        keyId,
                        payload 7uy,
                        cancellation
                     )
                     |> await)
                    |> ignore)
                "Settlement without intent is rejected"

            let intent =
                (store.Append(operation, None, Intent, keyId, payload 7uy, cancellation) |> await)

            let settlement =
                (store.Append(operation, None, SettledAccepted, keyId, payload 8uy, cancellation)
                 |> await)

            Expect.equal settlement.Sequence (intent.Sequence + 1L) "Settlement follows intent"

            Expect.throws
                (fun () ->
                    (store.Append(
                        operation,
                        None,
                        SettledRevoked,
                        keyId,
                        payload 9uy,
                        cancellation
                     )
                     |> await)
                    |> ignore)
                "Contradictory settlement is rejected"

            Expect.equal
                (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                2L
                "Only one settlement persisted"))


let writerCapabilityFence =
    testCase "[CC-WIT-001] writer capability and pending handoff fence every append" (fun _ ->
        fixture (fun owner writer identity capability ->
            let wrongCapability = RandomNumberGenerator.GetBytes(32)

            try
                use wrongStore = new Store(writer, identity, wrongCapability)

                Expect.throws
                    (fun () ->
                        (wrongStore.Append(
                            Guid.NewGuid(),
                            None,
                            Intent,
                            keyId,
                            payload 1uy,
                            cancellation
                         )
                         |> await)
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
                        (currentStore.Append(
                            Guid.NewGuid(),
                            None,
                            Intent,
                            keyId,
                            payload 2uy,
                            cancellation
                         )
                         |> await)
                        |> ignore)
                    "A pending handoff fences even the current writer"

                Expect.equal
                    (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                    0L
                    "Pending fence leaves no row"
            finally
                CryptographicOperations.ZeroMemory(wrongCapability)))
