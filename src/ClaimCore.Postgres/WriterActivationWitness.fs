namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal WriterActivationTickets = { Intent: Ticket; Settlement: Ticket }

/// Owner-only W2 append/readback; no runtime-writer grant can call this definer function.
module internal WriterActivationWitness =
    let private ownerAppend
        ownerConnection
        (witness: WitnessProtocol)
        handoffId
        activationId
        w1Sequence
        w1Hash
        (canonical: byte array)
        encryptedIntent
        encryptedSettlement
        =
        use connection = new NpgsqlConnection(ownerConnection)
        connection.Open()

        use command =
            new NpgsqlCommand(
                "SELECT intent_sequence,intent_hash,settlement_sequence,settlement_hash "
                + "FROM claimcore_witness.activate_writer_handoff("
                + "@installation,@lineage,@epoch,@handoff,@activation,@w1sequence,@w1hash,"
                + "@canonical,@key,@intent,@settlement)",
                connection
            )

        let identity = witness.Identity
        Sql.uuid command "installation" identity.InstallationId
        Sql.uuid command "lineage" identity.LineageId
        Sql.integer command "epoch" identity.Epoch
        Sql.uuid command "handoff" handoffId
        Sql.uuid command "activation" activationId
        Sql.integer command "w1sequence" w1Sequence
        Sql.add command "w1hash" NpgsqlDbType.Bytea (box w1Hash)
        Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
        Sql.uuid command "key" witness.KeyCustody.ActiveKeyId
        Sql.add command "intent" NpgsqlDbType.Bytea (box encryptedIntent)
        Sql.add command "settlement" NpgsqlDbType.Bytea (box encryptedSettlement)
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Writer activation tickets are unavailable."

        let first = reader.GetInt64(0), reader.GetFieldValue<byte array>(1)
        let second = reader.GetInt64(2), reader.GetFieldValue<byte array>(3)

        if reader.Read() || fst second <> fst first + 1L then
            invalidOp "Writer activation tickets are ambiguous."

        first, second

    let private readback
        (witness: WitnessProtocol)
        activationId
        (canonical: byte array)
        (firstSequence, firstHash)
        (secondSequence, secondHash)
        =
        let intent =
            witness.EvidenceStore.TryReadEvidence(activationId, Intent)
            |> Option.defaultWith (fun () -> raise WitnessPending)

        let settled =
            witness.EvidenceStore.TryReadEvidence(activationId, SettledAuthority)
            |> Option.defaultWith (fun () -> raise WitnessPending)

        let digest = SHA256.HashData(canonical)

        let check (evidence: Evidence) sequence hash phase expected aad =
            if
                evidence.Ticket.Sequence <> sequence
                || evidence.Ticket.EntryHash <> hash
                || evidence.Ticket.Phase <> phase
                || evidence.Ticket.ScopeKind <> Installation
                || evidence.Ticket.SubjectCaseId.IsSome
            then
                raise WitnessPending

            let plain =
                witness.KeyCustody.Decrypt(
                    evidence.Ticket.KeyId,
                    witness.AssociatedData(activationId, aad),
                    evidence.EncryptedPayload
                )

            try
                if plain <> expected then
                    raise WitnessPending
            finally
                CryptographicOperations.ZeroMemory(plain)

        check intent firstSequence firstHash Intent canonical "INTENT"
        check settled secondSequence secondHash SettledAuthority digest "SETTLED_AUTHORITY"

        if
            witness.TryReadHashAtSequence(firstSequence) <> Some firstHash
            || witness.TryReadHashAtSequence(secondSequence) <> Some secondHash
        then
            raise WitnessPending

        {
            Intent = intent.Ticket
            Settlement = settled.Ticket
        }

    let verifyHistorical witness activationId canonical intent settlement =
        readback witness activationId canonical intent settlement

    let verify witness activationId canonical intent settlement =
        let tickets = readback witness activationId canonical intent settlement
        let snapshot = witness.Snapshot()

        if
            snapshot.ActivationPending
            || snapshot.ActivationEventId <> Some activationId
            || snapshot.ActivationSequence <> Some tickets.Settlement.Sequence
            || snapshot.ActivationHash <> Some tickets.Settlement.EntryHash
        then
            raise WitnessPending

        tickets

    let activate
        ownerConnection
        (witness: WitnessProtocol)
        handoffId
        activationId
        w1Sequence
        w1Hash
        (canonical: byte array)
        =
        let keyId = witness.KeyCustody.ActiveKeyId
        let digest = SHA256.HashData(canonical)

        let encryptedIntent =
            witness.KeyCustody.Encrypt(
                keyId,
                witness.AssociatedData(activationId, "INTENT"),
                canonical
            )

        let encryptedSettlement =
            witness.KeyCustody.Encrypt(
                keyId,
                witness.AssociatedData(activationId, "SETTLED_AUTHORITY"),
                digest
            )

        try
            let first, second =
                ownerAppend
                    ownerConnection
                    witness
                    handoffId
                    activationId
                    w1Sequence
                    w1Hash
                    canonical
                    encryptedIntent
                    encryptedSettlement

            verify witness activationId canonical first second
        finally
            CryptographicOperations.ZeroMemory(digest)
            CryptographicOperations.ZeroMemory(encryptedIntent)
            CryptographicOperations.ZeroMemory(encryptedSettlement)
