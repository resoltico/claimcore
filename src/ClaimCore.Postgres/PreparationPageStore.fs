namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open OperationAuthorityStore
open PreparationData

/// Bounded recovery headers. Pending work and terminal evidence are deliberately distinct views:
/// a terminal revocation can outlive its payload-bearing preparation as a compact tombstone.
module internal PreparationPageStore =
    let private headerColumns =
        "p.operation_id, p.canonical_request_format, p.request_sha256, p.canonical_request, "
        + "p.prepared_at, p.prepared_application_version, p.preparing_contract_fingerprint, "
        + "p.preparing_contract_kind, l.state, l.recorded_at"

    let private pendingSql =
        "SELECT "
        + headerColumns
        + ", p.prepared_at AS occurred_at, 'PENDING'::text AS authority, "
        + "NULL::smallint AS revocation_format, NULL::text AS revocation_sha256, "
        + "NULL::timestamptz AS revoked_at, NULL::text AS revocation_reason "
        + "FROM claimcore.request_preparations p "
        + "LEFT JOIN claimcore.request_preparation_lifecycle l ON l.operation_id = p.operation_id "
        + "WHERE NOT EXISTS (SELECT 1 FROM claimcore.case_changes c "
        + "                  WHERE c.operation_id = p.operation_id) "
        + "AND NOT EXISTS (SELECT 1 FROM claimcore.operation_revocations r "
        + "                WHERE r.operation_id = p.operation_id) "

    let private terminalSql =
        "WITH terminal AS ("
        + "SELECT "
        + headerColumns
        + ", c.recorded_at AS occurred_at, 'ACCEPTED'::text AS authority, "
        + "NULL::smallint AS revocation_format, NULL::text AS revocation_sha256, "
        + "NULL::timestamptz AS revoked_at, NULL::text AS revocation_reason "
        + "FROM claimcore.request_preparations p "
        + "LEFT JOIN claimcore.request_preparation_lifecycle l ON l.operation_id = p.operation_id "
        + "JOIN claimcore.case_changes c ON c.operation_id = p.operation_id "
        + "UNION ALL "
        + "SELECT "
        + headerColumns
        + ", r.revoked_at AS occurred_at, 'REVOKED'::text AS authority, "
        + "r.canonical_request_format AS revocation_format, r.request_sha256 AS revocation_sha256, "
        + "r.revoked_at, r.reason AS revocation_reason "
        + "FROM claimcore.request_preparations p "
        + "LEFT JOIN claimcore.request_preparation_lifecycle l ON l.operation_id = p.operation_id "
        + "JOIN claimcore.operation_revocations r ON r.operation_id = p.operation_id "
        + "UNION ALL "
        + "SELECT r.operation_id, NULL::smallint, NULL::text, NULL::bytea, NULL::timestamptz, "
        + "NULL::text, NULL::text, NULL::text, NULL::text, NULL::timestamptz, "
        + "r.revoked_at AS occurred_at, 'REVOKED_TOMBSTONE'::text AS authority, "
        + "r.canonical_request_format AS revocation_format, r.request_sha256 AS revocation_sha256, "
        + "r.revoked_at, r.reason AS revocation_reason "
        + "FROM claimcore.operation_revocations r "
        + "WHERE NOT EXISTS (SELECT 1 FROM claimcore.request_preparations p "
        + "                  WHERE p.operation_id = r.operation_id)"
        + ") "
        + "SELECT * FROM terminal "

    let private contradictoryAuthority (connection: NpgsqlConnection) : Task<bool> =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.case_changes c "
                    + "JOIN claimcore.operation_revocations r ON r.operation_id = c.operation_id) "
                    + "OR EXISTS (SELECT 1 FROM claimcore.request_preparation_lifecycle l "
                    + "           LEFT JOIN claimcore.operation_revocations r "
                    + "             ON r.operation_id = l.operation_id "
                    + "           WHERE l.state = 'DISMISSED' AND r.operation_id IS NULL)",
                    connection
                )

            let! value = command.ExecuteScalarAsync()
            return value :?> bool
        }

    let private addCursor (command: NpgsqlCommand) (after: RecoveryCursor option) pageSize =
        Sql.optional
            command
            "afterOccurredAt"
            NpgsqlDbType.TimestampTz
            (after |> Option.map _.OccurredAt)

        Sql.optional command "afterOperation" NpgsqlDbType.Uuid (after |> Option.map _.OperationId)

        let limit = command.Parameters.Add("limit", NpgsqlDbType.Integer)
        limit.Value <- pageSize + 1

    let private pageSql view =
        match view with
        | RecoveryListView.Pending ->
            pendingSql
            + "AND (@afterOccurredAt IS NULL OR p.prepared_at < @afterOccurredAt "
            + "OR (p.prepared_at = @afterOccurredAt AND p.operation_id < @afterOperation)) "
            + "ORDER BY p.prepared_at DESC, p.operation_id DESC LIMIT @limit"
        | RecoveryListView.Terminal ->
            terminalSql
            + "WHERE (@afterOccurredAt IS NULL OR occurred_at < @afterOccurredAt "
            + "OR (occurred_at = @afterOccurredAt AND operation_id < @afterOperation)) "
            + "ORDER BY occurred_at DESC, operation_id DESC LIMIT @limit"

    let private authority =
        function
        | "PENDING" -> RecoveryAuthority.PendingAuthority
        | "ACCEPTED" -> RecoveryAuthority.AcceptedAuthority
        | "REVOKED" -> RecoveryAuthority.RevokedAuthority
        | _ -> raise (InvalidDataException("Stored recovery list authority is unknown."))

    let private row (reader: DbDataReader) : RecoveryStoreListItem * DateTimeOffset =
        let occurredAt = reader.GetFieldValue<DateTimeOffset>(10)

        match reader.GetString(11) with
        | "REVOKED_TOMBSTONE" ->
            let revocation =
                projected
                    (reader.GetGuid(0))
                    (reader.GetInt16(12) |> int)
                    (reader.GetString(13))
                    (reader.GetFieldValue<DateTimeOffset>(14))
                    (reader.GetString(15))

            RecoveryStoreListItem.Revoked revocation, occurredAt
        | token ->
            let header = read reader
            RecoveryStoreListItem.Retained(header, authority token), occurredAt

    let private readRows (reader: DbDataReader) =
        task {
            let found = ResizeArray<RecoveryStoreListItem * DateTimeOffset>()
            let mutable hasRow = true

            while hasRow do
                let! rowAvailable = reader.ReadAsync()
                hasRow <- rowAvailable

                if rowAvailable then
                    found.Add(row reader)

            return found
        }

    let private toPage
        view
        pageSize
        pendingCount
        pendingBytes
        (limits: PreparationLimits)
        (found: ResizeArray<RecoveryStoreListItem * DateTimeOffset>)
        : RecoveryStorePage =
        let items = found |> Seq.truncate pageSize |> Seq.toList

        {
            View = view
            Items = items |> List.map fst
            NextAfter =
                if found.Count > items.Length then
                    items
                    |> List.tryLast
                    |> Option.map (fun (item, occurredAt) ->
                        let operationId =
                            match item with
                            | RecoveryStoreListItem.Retained(header, _) -> header.OperationId
                            | RecoveryStoreListItem.Revoked value -> value.OperationId

                        {
                            View = view
                            OccurredAt = occurredAt
                            OperationId = operationId
                        })
                else
                    None
            PendingPreparationCount = pendingCount
            PendingCanonicalRequestBytes = pendingBytes
            MaximumPendingPreparations = limits.MaximumPreparations
            MaximumPendingCanonicalRequestBytes = limits.MaximumCanonicalRequestBytes
        }

    let list
        (connection: NpgsqlConnection)
        (limits: PreparationLimits)
        view
        (after: RecoveryCursor option)
        pageSize
        : Task<RecoveryStorePage> =
        task {
            if after |> Option.exists (fun (cursor: RecoveryCursor) -> cursor.View <> view) then
                return
                    raise (
                        InvalidDataException("Recovery cursor does not match the requested view.")
                    )
            else
                let! hasContradictoryAuthority = contradictoryAuthority connection

                if hasContradictoryAuthority then
                    return raise (InvalidDataException("Accepted and revoked authority coexist."))
                else
                    let! pendingCount64, pendingBytes = readPendingCapacity connection None

                    if pendingCount64 > int64 Int32.MaxValue then
                        return
                            raise (
                                InvalidDataException(
                                    "Pending recovery count exceeds its supported range."
                                )
                            )
                    else
                        use command = new NpgsqlCommand(pageSql view, connection)
                        addCursor command after pageSize
                        let! result = command.ExecuteReaderAsync()
                        use reader = result
                        let! found = readRows reader
                        return toPage view pageSize (int pendingCount64) pendingBytes limits found
        }
