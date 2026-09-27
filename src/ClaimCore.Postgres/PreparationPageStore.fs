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
    let private visiblePreparation =
        "AND NOT EXISTS (SELECT 1 FROM claimcore.case_erasure_tombstones t "
        + "WHERE t.case_id=p.case_id) "
        + "AND NOT EXISTS (SELECT 1 FROM claimcore.cases hidden "
        + "WHERE hidden.case_id=p.case_id "
        + "AND (hidden.disposition<>'ACTIVE' OR hidden.privacy_phase<>'ACTIVE')) "

    let private visibleRevocation =
        "AND NOT EXISTS (SELECT 1 FROM claimcore.case_erasure_tombstones t "
        + "WHERE t.case_id=r.case_id) "
        + "AND NOT EXISTS (SELECT 1 FROM claimcore.cases hidden "
        + "WHERE hidden.case_id=r.case_id "
        + "AND (hidden.disposition<>'ACTIVE' OR hidden.privacy_phase<>'ACTIVE')) "

    let private headerColumns =
        "p.operation_id, p.canonical_request_format, p.request_sha256, p.canonical_request, "
        + "p.prepared_at, p.prepared_application_version, p.preparing_contract_fingerprint, "
        + "p.preparing_contract_kind, p.case_id, p.preparer_actor_id, "
        + "p.preparer_grant_revision, p.importer_actor_id, l.state, l.recorded_at"

    let private pendingSql =
        "SELECT "
        + headerColumns
        + ", p.prepared_at AS occurred_at, 'PENDING'::text AS authority, "
        + "NULL::smallint AS revocation_format, NULL::text AS revocation_sha256, "
        + "NULL::timestamptz AS revoked_at, NULL::text AS revocation_reason, "
        + "NULL::uuid AS revoking_actor_id, NULL::bigint AS grant_revision, "
        + "NULL::uuid AS revocation_case_id "
        + "FROM claimcore.request_preparations p "
        + "LEFT JOIN claimcore.request_preparation_lifecycle l ON l.operation_id = p.operation_id "
        + "WHERE NOT EXISTS (SELECT 1 FROM claimcore.case_changes c "
        + "                  WHERE c.operation_id = p.operation_id) "
        + "AND NOT EXISTS (SELECT 1 FROM claimcore.operation_revocations r "
        + "                WHERE r.operation_id = p.operation_id) "
        + visiblePreparation

    let private terminalSql =
        "WITH terminal AS ("
        + "SELECT "
        + headerColumns
        + ", c.recorded_at AS occurred_at, 'ACCEPTED'::text AS authority, "
        + "NULL::smallint AS revocation_format, NULL::text AS revocation_sha256, "
        + "NULL::timestamptz AS revoked_at, NULL::text AS revocation_reason, "
        + "NULL::uuid AS revoking_actor_id, NULL::bigint AS grant_revision, "
        + "NULL::uuid AS revocation_case_id "
        + "FROM claimcore.request_preparations p "
        + "LEFT JOIN claimcore.request_preparation_lifecycle l ON l.operation_id = p.operation_id "
        + "JOIN claimcore.case_changes c ON c.operation_id = p.operation_id "
        + "WHERE 1=1 "
        + visiblePreparation
        + "UNION ALL "
        + "SELECT "
        + headerColumns
        + ", r.revoked_at AS occurred_at, 'REVOKED'::text AS authority, "
        + "r.canonical_request_format AS revocation_format, r.request_sha256 AS revocation_sha256, "
        + "r.revoked_at, r.reason AS revocation_reason, "
        + "r.revoking_actor_id, r.grant_revision, r.case_id AS revocation_case_id "
        + "FROM claimcore.request_preparations p "
        + "LEFT JOIN claimcore.request_preparation_lifecycle l ON l.operation_id = p.operation_id "
        + "JOIN claimcore.operation_revocations r ON r.operation_id = p.operation_id "
        + "WHERE 1=1 "
        + visiblePreparation
        + "UNION ALL "
        + "SELECT r.operation_id, NULL::smallint, NULL::text, NULL::bytea, NULL::timestamptz, "
        + "NULL::text, NULL::text, NULL::text, NULL::uuid, NULL::uuid, "
        + "NULL::bigint, NULL::uuid, NULL::text, NULL::timestamptz, "
        + "r.revoked_at AS occurred_at, 'REVOKED_TOMBSTONE'::text AS authority, "
        + "r.canonical_request_format AS revocation_format, r.request_sha256 AS revocation_sha256, "
        + "r.revoked_at, r.reason AS revocation_reason, "
        + "r.revoking_actor_id, r.grant_revision, r.case_id AS revocation_case_id "
        + "FROM claimcore.operation_revocations r "
        + "WHERE NOT EXISTS (SELECT 1 FROM claimcore.request_preparations p "
        + "                  WHERE p.operation_id = r.operation_id) "
        + visibleRevocation
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

    let private visiblePendingCapacity (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*),COALESCE(sum(octet_length(p.canonical_request)),0)::bigint "
                    + "FROM claimcore.request_preparations p "
                    + "WHERE NOT EXISTS (SELECT 1 FROM claimcore.case_changes c "
                    + "WHERE c.operation_id=p.operation_id) "
                    + "AND NOT EXISTS (SELECT 1 FROM claimcore.operation_revocations r "
                    + "WHERE r.operation_id=p.operation_id) "
                    + visiblePreparation,
                    connection
                )

            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                invalidOp "Visible recovery capacity is unavailable."

            return reader.GetInt64(0), reader.GetInt64(1)
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
        let occurredAt = reader.GetFieldValue<DateTimeOffset>(14)

        match reader.GetString(15) with
        | "REVOKED_TOMBSTONE" ->
            let revocation =
                projected
                    (reader.GetGuid(0))
                    (reader.GetInt16(16) |> int)
                    (reader.GetString(17))
                    (reader.GetFieldValue<DateTimeOffset>(18))
                    (reader.GetString(19))
                    (reader.GetGuid(20))
                    (reader.GetInt64(21))
                    (reader.GetGuid(22))

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
        (actor: ActorBinding)
        expiresAt
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
                            ActorId = actor.ActorId
                            GrantRevision = actor.GrantRevision
                            ExpiresAt = expiresAt
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
        (actor: ActorBinding)
        expiresAt
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
                    let! pendingCount64, pendingBytes = visiblePendingCapacity connection

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

                        return
                            toPage
                                view
                                pageSize
                                actor
                                expiresAt
                                (int pendingCount64)
                                pendingBytes
                                limits
                                found
        }
