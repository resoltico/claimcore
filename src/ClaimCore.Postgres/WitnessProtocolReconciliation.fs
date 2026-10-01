namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

module internal WitnessProtocolReconciliation =
    let private revokedMatches
        operationId
        (ticket: Ticket)
        (plain: byte array)
        (reader: DbDataReader)
        =
        let expected =
            WitnessCandidate.revoked
                operationId
                (reader.GetString(0))
                {
                    CaseId = reader.GetGuid(6)
                    RevokingActorId = reader.GetGuid(4)
                    GrantRevision = reader.GetInt64(5)
                }

        try
            plain = expected
            && reader.GetInt64(1) = ticket.Sequence
            && reader.GetInt64(2) = ticket.Epoch
            && reader.GetFieldValue<byte array>(3) = ticket.EntryHash
            && not (reader.Read())
        finally
            CryptographicOperations.ZeroMemory(expected)

    let private acceptedIntent
        (witness: WitnessProtocol)
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operationId
        =
        let store = witness.EvidenceStore
        let custody = witness.KeyCustody

        let associatedData operation phase =
            witness.AssociatedData(operation, phase)

        let intent =
            store.TryReadEvidence(operationId, Intent)
            |> Option.defaultWith (fun () -> raise WitnessPending)

        let plain =
            custody.Decrypt(
                intent.Ticket.KeyId,
                associatedData operationId "INTENT",
                intent.EncryptedPayload
            )

        try
            use document = JsonDocument.Parse(plain)
            let root = document.RootElement

            use command =
                new NpgsqlCommand(
                    "SELECT case_reference,revision,canonical_request,snapshot,"
                    + "effective_business_date::text,observed_utc_instant,rule_revision,"
                    + "witness_sequence,witness_epoch,witness_entry_hash,case_id,"
                    + "preparer_actor_id,importer_actor_id,submitter_actor_id,resolver_actor_id,"
                    + "accepted_actor_id,grant_revision "
                    + "FROM claimcore.case_changes WHERE operation_id=@operation",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId)
            |> ignore

            use reader = command.ExecuteReader()

            if not (reader.Read()) then
                raise WitnessPending

            if
                not (WitnessPrimaryMatch.accepted operationId intent.Ticket root reader)
                || reader.Read()
            then
                raise WitnessPending

            reader.Close()

            {
                Ticket = intent.Ticket
                CandidateHash = SHA256.HashData(plain)
            }
        finally
            CryptographicOperations.ZeroMemory(plain)

    type WitnessProtocol with
        member this.VerifyHistoricalTip(sequence: int64, expectedHash: byte array) =
            if isNull (box expectedHash) || expectedHash.Length <> 32 then
                raise WitnessPending

            match this.EvidenceStore.TryReadEntryHash(sequence) with
            | Some observed when
                CryptographicOperations.FixedTimeEquals(
                    ReadOnlySpan<byte>(observed),
                    ReadOnlySpan<byte>(expectedHash)
                )
                ->
                ()
            | _ -> raise WitnessPending

        /// Caller must obtain these values from a committed primary authority row under its lock.
        member this.ReconcileAuthority
            (
                operationId: Guid,
                sequence: int64,
                epoch: int64,
                entryHash: byte array,
                exactCanonicalAction: byte array
            ) =
            let intent =
                this.EvidenceStore.TryReadEvidence(operationId, Intent)
                |> Option.defaultWith (fun () -> raise WitnessPending)

            if
                intent.Ticket.Sequence <> sequence
                || intent.Ticket.Epoch <> epoch
                || intent.Ticket.EntryHash <> entryHash
            then
                raise WitnessPending

            let plain =
                this.KeyCustody.Decrypt(
                    intent.Ticket.KeyId,
                    this.AssociatedData(operationId, "INTENT"),
                    intent.EncryptedPayload
                )

            try
                if plain <> exactCanonicalAction then
                    raise WitnessPending

                let recovered =
                    {
                        Ticket = intent.Ticket
                        CandidateHash = SHA256.HashData(plain)
                    }

                this.SettleAuthority(operationId, recovered) |> ignore

                this.VerifyAuthorityEvidence(
                    operationId,
                    sequence,
                    epoch,
                    entryHash,
                    recovered.CandidateHash
                )
            finally
                CryptographicOperations.ZeroMemory(plain)

        member this.VerifyAccepted
            (connection: NpgsqlConnection, transaction: NpgsqlTransaction, operationId: Guid)
            =
            let intent = acceptedIntent this connection transaction operationId

            this.VerifyAcceptedEvidence(
                operationId,
                intent.Ticket.Sequence,
                intent.Ticket.Epoch,
                intent.Ticket.EntryHash,
                intent.CandidateHash
            )

        member this.ReconcileAccepted
            (connection: NpgsqlConnection, transaction: NpgsqlTransaction, operationId: Guid)
            =
            try
                let intent = acceptedIntent this connection transaction operationId
                this.SettleAccepted(operationId, intent) |> ignore

                this.VerifyAcceptedEvidence(
                    operationId,
                    intent.Ticket.Sequence,
                    intent.Ticket.Epoch,
                    intent.Ticket.EntryHash,
                    intent.CandidateHash
                )
            with _ ->
                raise WitnessPending

        member this.ReconcileRevoked
            (connection: NpgsqlConnection, transaction: NpgsqlTransaction, operationId: Guid)
            =
            let eventId = WitnessEventIdentity.revocationEventId operationId
            let store = this.EvidenceStore
            let custody = this.KeyCustody
            let associatedData operation phase = this.AssociatedData(operation, phase)

            let intent =
                store.TryReadEvidence(eventId, Intent)
                |> Option.defaultWith (fun () -> raise WitnessPending)

            let plain =
                custody.Decrypt(
                    intent.Ticket.KeyId,
                    associatedData eventId "INTENT",
                    intent.EncryptedPayload
                )

            try
                use command =
                    new NpgsqlCommand(
                        "SELECT request_sha256,witness_sequence,witness_epoch,witness_entry_hash,"
                        + "revoking_actor_id,grant_revision,case_id,witness_event_id "
                        + "FROM claimcore.operation_revocations WHERE operation_id=@operation",
                        connection,
                        transaction
                    )

                command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId)
                |> ignore

                use reader = command.ExecuteReader()

                if not (reader.Read()) then
                    raise WitnessPending

                if
                    reader.GetGuid(7) <> eventId
                    || not (revokedMatches operationId intent.Ticket plain reader)
                then
                    raise WitnessPending

                reader.Close()

                let recovered =
                    {
                        Ticket = intent.Ticket
                        CandidateHash = SHA256.HashData(plain)
                    }

                this.SettleRevoked(eventId, recovered) |> ignore
                this.RequireSettled(eventId, SettledRevoked)
            finally
                CryptographicOperations.ZeroMemory(plain)
