namespace ClaimCore.Postgres

open System
open System.Threading
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
        (ct: CancellationToken)
        =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)

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
            ct.ThrowIfCancellationRequested()
            use! reader = command.ExecuteReaderAsync(CancellationToken.None)

            let! found = reader.ReadAsync(CancellationToken.None)

            if not found then
                invalidOp "Writer activation tickets are unavailable."

            let first = reader.GetInt64(0), reader.GetFieldValue<byte array>(1)
            let second = reader.GetInt64(2), reader.GetFieldValue<byte array>(3)

            let! duplicated = reader.ReadAsync(CancellationToken.None)

            if duplicated || fst second <> fst first + 1L then
                invalidOp "Writer activation tickets are ambiguous."

            return first, second
        }

    let private readback
        (witness: WitnessProtocol)
        activationId
        (canonical: byte array)
        (firstSequence, firstHash)
        (secondSequence, secondHash)
        (ct: CancellationToken)
        =
        task {
            let! readIntent = witness.EvidenceStore.TryReadEvidence(activationId, Intent, ct)
            let intent = readIntent |> Option.defaultWith (fun () -> raise WitnessPending)

            let! readSettlement =
                witness.EvidenceStore.TryReadEvidence(activationId, SettledAuthority, ct)

            let settled = readSettlement |> Option.defaultWith (fun () -> raise WitnessPending)

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

            let! firstObserved = witness.TryReadHashAtSequence(firstSequence, ct)
            let! secondObserved = witness.TryReadHashAtSequence(secondSequence, ct)

            if firstObserved <> Some firstHash || secondObserved <> Some secondHash then
                raise WitnessPending

            return
                {
                    Intent = intent.Ticket
                    Settlement = settled.Ticket
                }
        }

    let verifyHistorical witness activationId canonical intent settlement ct =
        readback witness activationId canonical intent settlement ct

    let verify witness activationId canonical intent settlement ct =
        task {
            let! tickets = readback witness activationId canonical intent settlement ct
            let! snapshot = witness.Snapshot(ct)

            if
                snapshot.ActivationPending
                || snapshot.ActivationEventId <> Some activationId
                || snapshot.ActivationSequence <> Some tickets.Settlement.Sequence
                || snapshot.ActivationHash <> Some tickets.Settlement.EntryHash
            then
                raise WitnessPending

            return tickets
        }

    let activate
        ownerConnection
        (witness: WitnessProtocol)
        handoffId
        activationId
        w1Sequence
        w1Hash
        (canonical: byte array)
        (ct: CancellationToken)
        =
        task {
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
                let! first, second =
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
                        ct

                return! verify witness activationId canonical first second CancellationToken.None
            finally
                CryptographicOperations.ZeroMemory(digest)
                CryptographicOperations.ZeroMemory(encryptedIntent)
                CryptographicOperations.ZeroMemory(encryptedSettlement)
        }
