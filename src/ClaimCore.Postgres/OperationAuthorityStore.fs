namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.Witness

/// Durable operation revocations outlive optional technical preparation rows. This module owns no
/// business decision; it only reads and appends exact operation authority evidence.
module internal OperationAuthorityStore =
    [<RequireQualifiedAccess>]
    type RevocationReason = | OperatorDismissal

    [<NoEquality; NoComparison>]
    type Revocation =
        {
            OperationId: Guid
            WitnessEventId: Guid
            CaseId: Guid
            RevokingActorId: Guid
            GrantRevision: int64
            CanonicalRequestFormat: int
            RequestSha256: string
            RevokedAt: DateTimeOffset
            Reason: RevocationReason
        }

    let private reason =
        function
        | RevocationReason.OperatorDismissal -> "OPERATOR_DISMISSAL"

    let private readReason =
        function
        | "OPERATOR_DISMISSAL" -> RevocationReason.OperatorDismissal
        | _ -> raise (InvalidDataException("Stored operation revocation reason is unknown."))

    let private read (reader: DbDataReader) =
        let format = reader.GetInt16(1) |> int
        let digest = reader.GetString(2)

        if
            reader.GetGuid(0) = Guid.Empty
            || format <> int RecordVersions.CanonicalCommandFormat
            || digest.Length <> 64
            || reader.GetGuid(5) = Guid.Empty
            || reader.GetInt64(6) <= 0L
            || reader.GetGuid(7) = Guid.Empty
            || reader.GetGuid(8) <> WitnessEventIdentity.revocationEventId (reader.GetGuid(0))
        then
            raise (InvalidDataException("Stored operation revocation failed integrity checks."))

        {
            OperationId = reader.GetGuid(0)
            WitnessEventId = reader.GetGuid(8)
            CaseId = reader.GetGuid(7)
            RevokingActorId = reader.GetGuid(5)
            GrantRevision = reader.GetInt64(6)
            CanonicalRequestFormat = format
            RequestSha256 = digest
            RevokedAt = reader.GetFieldValue<DateTimeOffset>(3)
            Reason = reader.GetString(4) |> readReason
        }

    let find
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        : Task<Revocation option> =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT operation_id, canonical_request_format, request_sha256, revoked_at, reason, "
                    + "revoking_actor_id,grant_revision,case_id,witness_event_id "
                    + "FROM claimcore.operation_revocations WHERE operation_id = @operation",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! exists = reader.ReadAsync()
            return if exists then Some(read reader) else None
        }

    let findRead (connection: NpgsqlConnection) (operationId: Guid) : Task<Revocation option> =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT operation_id, canonical_request_format, request_sha256, revoked_at, reason, "
                    + "revoking_actor_id,grant_revision,case_id,witness_event_id "
                    + "FROM claimcore.operation_revocations WHERE operation_id = @operation",
                    connection
                )

            Sql.uuid command "operation" operationId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! exists = reader.ReadAsync()
            return if exists then Some(read reader) else None
        }

    let matches requestSha256 (revocation: Revocation) =
        revocation.CanonicalRequestFormat = int RecordVersions.CanonicalCommandFormat
        && String.Equals(revocation.RequestSha256, requestSha256, StringComparison.Ordinal)

    let project (revocation: Revocation) : OperationRevocation =
        {
            OperationId = revocation.OperationId
            CaseId = revocation.CaseId
            RevokingActorId = revocation.RevokingActorId
            GrantRevision = revocation.GrantRevision
            CanonicalRequestFormat = revocation.CanonicalRequestFormat
            RequestSha256 = revocation.RequestSha256
            RevokedAt = revocation.RevokedAt
            Reason = reason revocation.Reason
        }

    let projected
        (operationId: Guid)
        canonicalRequestFormat
        requestSha256
        revokedAt
        reasonValue
        revokingActorId
        grantRevision
        caseId
        witnessEventId
        : OperationRevocation =
        let revocation =
            {
                OperationId = operationId
                WitnessEventId = witnessEventId
                CaseId = caseId
                RevokingActorId = revokingActorId
                GrantRevision = grantRevision
                CanonicalRequestFormat = canonicalRequestFormat
                RequestSha256 = requestSha256
                RevokedAt = revokedAt
                Reason = readReason reasonValue
            }

        if
            revocation.OperationId = Guid.Empty
            || revocation.WitnessEventId <> WitnessEventIdentity.revocationEventId operationId
            || revocation.CanonicalRequestFormat <> int RecordVersions.CanonicalCommandFormat
            || revocation.RequestSha256.Length <> 64
            || revocation.RevokingActorId = Guid.Empty
            || revocation.GrantRevision <= 0L
            || revocation.CaseId = Guid.Empty
        then
            raise (InvalidDataException("Stored operation revocation failed integrity checks."))

        project revocation

    let insert
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        (requestSha256: string)
        (actorEvidence: RevocationActorEvidence)
        (reasonValue: RevocationReason)
        (ticket: Ticket)
        : Task<Revocation option> =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.operation_revocations ("
                    + "operation_id, witness_event_id, case_id, canonical_request_format, request_sha256, "
                    + "revoking_actor_id,grant_revision,reason, "
                    + "witness_sequence, witness_epoch, witness_entry_hash"
                    + ") VALUES (@operation, @event, @caseId, @format, @digest, @actor, @grantRevision, "
                    + "@reason, @sequence, @epoch, @hash) "
                    + "ON CONFLICT (operation_id) DO NOTHING "
                    + "RETURNING operation_id, canonical_request_format, request_sha256, "
                    + "revoked_at, reason, revoking_actor_id, grant_revision, case_id, witness_event_id",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            Sql.uuid command "event" ticket.OperationId
            Sql.uuid command "caseId" actorEvidence.CaseId

            Sql.add
                command
                "format"
                NpgsqlDbType.Smallint
                (box (int16 RecordVersions.CanonicalCommandFormat))

            Sql.text command "digest" requestSha256
            Sql.uuid command "actor" actorEvidence.RevokingActorId
            Sql.integer command "grantRevision" actorEvidence.GrantRevision
            Sql.text command "reason" (reason reasonValue)
            Sql.integer command "sequence" ticket.Sequence
            Sql.integer command "epoch" ticket.Epoch
            Sql.add command "hash" NpgsqlDbType.Bytea (box ticket.EntryHash)
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! inserted = reader.ReadAsync()
            return if inserted then Some(read reader) else None
        }
