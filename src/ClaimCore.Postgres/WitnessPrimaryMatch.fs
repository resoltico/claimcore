namespace ClaimCore.Postgres

open Npgsql
open System.Threading
open NpgsqlTypes

open System
open System.Security.Cryptography
open System.Data.Common
open System.Text.Json
open ClaimCore.Witness
open ClaimCore.Domain

/// Read-only, bounded comparison of one committed primary row with its encrypted candidate.
module internal WitnessPrimaryMatch =
    let private optionalActor (root: JsonElement) (name: string) (reader: DbDataReader) ordinal =
        let value = root.GetProperty(name)

        if reader.IsDBNull(ordinal) then
            value.ValueKind = JsonValueKind.Null
        else
            value.ValueKind = JsonValueKind.String
            && value.GetGuid() = reader.GetGuid(ordinal)

    let private attribution (root: JsonElement) (reader: DbDataReader) =
        root.GetProperty("preparerActorId").GetGuid() = reader.GetGuid(11)
        && optionalActor root "importerActorId" reader 12
        && optionalActor root "submitterActorId" reader 13
        && optionalActor root "resolverActorId" reader 14
        && root.GetProperty("acceptedActorId").GetGuid() = reader.GetGuid(15)
        && root.GetProperty("grantRevision").GetInt64() = reader.GetInt64(16)

    let private identity (root: JsonElement) (reader: DbDataReader) operationId =
        root.GetProperty("version").GetInt32() = 2
        && root.GetProperty("ruleRevision").GetInt32() = DomainRules.version
        && reader.GetInt16(6) = int16 DomainRules.version
        && root.GetProperty("operationId").GetGuid() = operationId
        && root.GetProperty("caseId").GetGuid() = reader.GetGuid(10)
        && root.GetProperty("caseReference").GetString() = reader.GetString(0)
        && root.GetProperty("newRevision").GetInt64() = reader.GetInt64(1)
        && root.GetProperty("expectedRevision").GetInt64() = reader.GetInt64(1) - 1L

    let private payload (root: JsonElement) (reader: DbDataReader) =
        let captured = root.GetProperty("observedUtcInstant").GetDateTimeOffset()
        let observed = reader.GetFieldValue<DateTimeOffset>(5)

        root.GetProperty("canonicalRequest").GetBytesFromBase64() =
            reader.GetFieldValue<byte array>(2)
        && root.GetProperty("canonicalSnapshot").GetBytesFromBase64() =
            reader.GetFieldValue<byte array>(3)
        && root.GetProperty("effectiveBusinessDate").GetString() = reader.GetString(4)
        && abs (captured.Subtract(observed).Ticks) <= 10L

    let private ticket (ticket: Ticket) (reader: DbDataReader) =
        reader.GetInt64(7) = ticket.Sequence
        && reader.GetInt64(8) = ticket.Epoch
        && reader.GetFieldValue<byte array>(9) = ticket.EntryHash

    let accepted operationId intentTicket root (reader: DbDataReader) =
        identity root reader operationId
        && attribution root reader
        && payload root reader
        && ticket intentTicket reader

    let requireAccepted
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operationId
        intentTicket
        root
        (ct: CancellationToken)
        =
        task {
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

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                raise WitnessPending

            let matches = accepted operationId intentTicket root reader
            let! duplicated = reader.ReadAsync(ct)

            if not matches || duplicated then
                raise WitnessPending

            reader.Close()

        }

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

        finally
            CryptographicOperations.ZeroMemory(expected)


    let requireRevoked
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operationId
        eventId
        intentTicket
        plain
        (ct: CancellationToken)
        =
        task {
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

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                raise WitnessPending

            let matches =
                reader.GetGuid(7) = eventId
                && revokedMatches operationId intentTicket plain reader

            let! duplicated = reader.ReadAsync(ct)

            if not matches || duplicated then
                raise WitnessPending

            reader.Close()

        }
