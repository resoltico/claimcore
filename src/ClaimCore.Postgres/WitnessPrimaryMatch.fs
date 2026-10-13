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
    /// Immutable bounded primary values; acquisition and proof checking share this shape.
    type Accepted =
        {
            Reference: string
            Revision: int64
            Request: byte array
            Snapshot: byte array
            BusinessDate: string
            Observed: DateTimeOffset
            Rule: int16
            Sequence: int64
            Epoch: int64
            Hash: byte array
            CaseId: Guid
            Preparer: Guid
            Importer: Guid option
            Submitter: Guid option
            Resolver: Guid option
            Actor: Guid
            GrantRevision: int64
        }

    let readAccepted (reader: DbDataReader) =
        let optional index =
            if reader.IsDBNull index then
                None
            else
                Some(reader.GetGuid index)

        {
            Reference = reader.GetString(0)
            Revision = reader.GetInt64(1)
            Request = reader.GetFieldValue<byte array>(2)
            Snapshot = reader.GetFieldValue<byte array>(3)
            BusinessDate = ScalarEncoding.dateText (reader.GetInt32(4))
            Observed = reader.GetFieldValue<DateTimeOffset>(5)
            Rule = reader.GetInt16(6)
            Sequence = reader.GetInt64(7)
            Epoch = reader.GetInt64(8)
            Hash = reader.GetFieldValue<byte array>(9)
            CaseId = reader.GetGuid(10)
            Preparer = reader.GetGuid(11)
            Importer = optional 12
            Submitter = optional 13
            Resolver = optional 14
            Actor = reader.GetGuid(15)
            GrantRevision = reader.GetInt64(16)
        }

    let private optionalActor (root: JsonElement) name expected =
        let value = root.GetProperty(name: string)

        match expected with
        | None -> value.ValueKind = JsonValueKind.Null
        | Some actor -> value.ValueKind = JsonValueKind.String && value.GetGuid() = actor

    let accepted operationId (intentTicket: Ticket) (root: JsonElement) (row: Accepted) =
        root.GetProperty("version").GetInt32() = 2
        && root.GetProperty("ruleRevision").GetInt32() = DomainRules.version
        && row.Rule = int16 DomainRules.version
        && root.GetProperty("operationId").GetGuid() = operationId
        && root.GetProperty("caseId").GetGuid() = row.CaseId
        && root.GetProperty("caseReference").GetString() = row.Reference
        && root.GetProperty("newRevision").GetInt64() = row.Revision
        && root.GetProperty("expectedRevision").GetInt64() = row.Revision - 1L
        && root.GetProperty("preparerActorId").GetGuid() = row.Preparer
        && optionalActor root "importerActorId" row.Importer
        && optionalActor root "submitterActorId" row.Submitter
        && optionalActor root "resolverActorId" row.Resolver
        && root.GetProperty("acceptedActorId").GetGuid() = row.Actor
        && root.GetProperty("grantRevision").GetInt64() = row.GrantRevision
        && root.GetProperty("canonicalRequest").GetBytesFromBase64() = row.Request
        && root.GetProperty("canonicalSnapshot").GetBytesFromBase64() = row.Snapshot
        && root.GetProperty("effectiveBusinessDate").GetString() = row.BusinessDate
        && abs (
            root.GetProperty("observedUtcInstant").GetDateTimeOffset().Subtract(row.Observed).Ticks
           )
           <= 10L
        && row.Sequence = intentTicket.Sequence
        && row.Epoch = intentTicket.Epoch
        && row.Hash = intentTicket.EntryHash

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
                    + "effective_business_date - DATE '2000-01-01',observed_utc_instant,rule_revision,"
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

            let matches = accepted operationId intentTicket root (readAccepted reader)
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
