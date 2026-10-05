namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql

/// Read-only primary/witness activation proof for a restarted case-work runtime.
module internal WriterActivationRead =
    let private primaryRow
        (connection: NpgsqlConnection)
        handoffId
        generation
        w1Sequence
        w1Hash
        activationId
        activationSequence
        activationHash
        epoch
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT handoff_id,writer_generation,w1_sequence,w1_hash,canonical_action,"
                    + "candidate_sha256,witness_intent_sequence,witness_intent_hash,"
                    + "witness_sequence,witness_epoch,witness_entry_hash "
                    + "FROM claimcore.writer_activations WHERE activation_id=@activation",
                    connection
                )

            Sql.uuid command "activation" activationId

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                invalidOp "Restored writer activation event is absent."

            let candidate = reader.GetFieldValue<byte array>(4)
            let digest = SHA256.HashData(candidate)
            let intentSequence = reader.GetInt64(6)
            let intentHash = reader.GetFieldValue<byte array>(7)
            let settledSequence = reader.GetInt64(8)
            let settledHash = reader.GetFieldValue<byte array>(10)

            let matching =
                reader.GetGuid(0) = handoffId
                && reader.GetInt64(1) > 1L
                && reader.GetInt64(1) = generation
                && reader.GetInt64(2) = w1Sequence
                && reader.GetFieldValue<byte array>(3) = w1Hash
                && reader.GetFieldValue<byte array>(5) = digest
                && settledSequence = activationSequence
                && settledHash = activationHash
                && reader.GetInt64(9) = epoch
                && settledSequence = intentSequence + 1L

            let! duplicated = reader.ReadAsync(ct)

            if duplicated || not matching then
                invalidOp "Restored writer activation event diverged."

            return candidate, intentSequence, intentHash, settledSequence, settledHash
        }

    let verify
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        handoffId
        generation
        w1Sequence
        w1Hash
        activationId
        activationSequence
        activationHash
        (ct: CancellationToken)
        =
        task {
            let! candidate, intentSequence, intentHash, settledSequence, settledHash =
                primaryRow
                    connection
                    handoffId
                    generation
                    w1Sequence
                    w1Hash
                    activationId
                    activationSequence
                    activationHash
                    witness.Identity.Epoch
                    ct

            let! _ =
                WriterActivationWitness.verify
                    witness
                    activationId
                    candidate
                    (intentSequence, intentHash)
                    (settledSequence, settledHash)
                    ct

            return ()
        }
