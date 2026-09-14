namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.RecordFormat

/// Durable operation revocations outlive optional technical preparation rows. This module owns no
/// business decision; it only reads and appends exact operation authority evidence.
module internal OperationAuthorityStore =
    [<RequireQualifiedAccess>]
    type RevocationReason =
        | OperatorDismissal
        | LegacyDismissal

    [<NoEquality; NoComparison>]
    type Revocation =
        {
            OperationId: Guid
            CanonicalRequestFormat: int
            RequestSha256: string
            RevokedAt: DateTimeOffset
            Reason: RevocationReason
        }

    let private reason =
        function
        | RevocationReason.OperatorDismissal -> "OPERATOR_DISMISSAL"
        | RevocationReason.LegacyDismissal -> "LEGACY_DISMISSAL"

    let private readReason =
        function
        | "OPERATOR_DISMISSAL" -> RevocationReason.OperatorDismissal
        | "LEGACY_DISMISSAL" -> RevocationReason.LegacyDismissal
        | _ -> raise (InvalidDataException("Stored operation revocation reason is unknown."))

    let private read (reader: DbDataReader) =
        let format = reader.GetInt16(1) |> int
        let digest = reader.GetString(2)

        if
            reader.GetGuid(0) = Guid.Empty
            || format <> int RecordVersions.CanonicalCommandFormat
            || digest.Length <> 64
        then
            raise (InvalidDataException("Stored operation revocation failed integrity checks."))

        {
            OperationId = reader.GetGuid(0)
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
                    "SELECT operation_id, canonical_request_format, request_sha256, revoked_at, reason "
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
                    "SELECT operation_id, canonical_request_format, request_sha256, revoked_at, reason "
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
        : OperationRevocation =
        let revocation =
            {
                OperationId = operationId
                CanonicalRequestFormat = canonicalRequestFormat
                RequestSha256 = requestSha256
                RevokedAt = revokedAt
                Reason = readReason reasonValue
            }

        if
            revocation.OperationId = Guid.Empty
            || revocation.CanonicalRequestFormat <> int RecordVersions.CanonicalCommandFormat
            || revocation.RequestSha256.Length <> 64
        then
            raise (InvalidDataException("Stored operation revocation failed integrity checks."))

        project revocation

    let insert
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        (requestSha256: string)
        (reasonValue: RevocationReason)
        : Task<Revocation option> =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.operation_revocations ("
                    + "operation_id, canonical_request_format, request_sha256, reason"
                    + ") VALUES (@operation, @format, @digest, @reason) "
                    + "ON CONFLICT (operation_id) DO NOTHING "
                    + "RETURNING operation_id, canonical_request_format, request_sha256, revoked_at, reason",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId

            Sql.add
                command
                "format"
                NpgsqlDbType.Smallint
                (box (int16 RecordVersions.CanonicalCommandFormat))

            Sql.text command "digest" requestSha256
            Sql.text command "reason" (reason reasonValue)
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! inserted = reader.ReadAsync()
            return if inserted then Some(read reader) else None
        }
