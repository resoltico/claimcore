namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading
open Npgsql
open NpgsqlTypes
open WitnessProtocolReconciliation

[<RequireQualifiedAccess>]
type internal LifecycleReconcileOutcome =
    | Settled of Guid
    | PrimaryAbsentUnknown of Guid
    | IntegrityMismatch of Guid
    | Unconfirmed of Guid

type internal LifecycleReconcilePage =
    {
        Outcomes: LifecycleReconcileOutcome list
        NextAfter: Guid option
    }

/// Owner-only recovery of an existing committed lifecycle authority row. No primary write,
/// ticket replacement, guessed abort, or runtime facade is involved.
module internal CaseLifecycleReconcile =
    let private invalid () =
        raise (InvalidDataException("Lifecycle reconciliation evidence differs."))

    let private requireInstallation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT installation_id,lineage_id,witness_epoch "
                    + "FROM claimcore.installation_lineage WHERE singleton",
                    connection,
                    transaction
                )

            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let! found = reader.ReadAsync(ct)
            let identity = witness.Identity

            if
                not found
                || reader.GetGuid(0) <> identity.InstallationId
                || reader.GetGuid(1) <> identity.LineageId
                || reader.GetInt64(2) <> identity.Epoch
            then
                invalid ()

            let! extra = reader.ReadAsync(ct)

            if extra then
                invalid ()
        }

    let private previousHash
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (row: LifecycleAuditEventRow)
        (ct: CancellationToken)
        =
        task {
            if row.Sequence = 1L then
                return Array.zeroCreate<byte> 32
            else
                use command =
                    new NpgsqlCommand(
                        "SELECT event_hash FROM claimcore.case_lifecycle_events "
                        + "WHERE case_id=@caseId AND lifecycle_sequence=@sequence",
                        connection,
                        transaction
                    )

                Sql.uuid command "caseId" row.CaseId
                Sql.integer command "sequence" (row.Sequence - 1L)
                let! value = command.ExecuteScalarAsync(ct)

                match value with
                | :? (byte array) as hash -> return hash
                | _ -> return invalid ()
        }

    let private settleEvent
        connection
        transaction
        (witness: WitnessProtocol)
        (row: LifecycleAuditEventRow)
        (ct: CancellationToken)
        =
        task {
            let! previous = previousHash connection transaction row ct

            if previous <> row.PreviousHash then
                invalid ()

            CaseLifecycleAuditEvidence.validateEvent
                Int64.MaxValue
                row.CaseId
                previous
                row.Sequence
                row
            |> ignore

            do!
                witness.ReconcileAuthority(
                    row.EventId,
                    row.WitnessSequence,
                    row.WitnessEpoch,
                    row.WitnessHash,
                    row.Canonical,
                    ct
                )
        }

    let private settleApproval (witness: WitnessProtocol) (row: LifecycleAuditApprovalRow) ct =
        task {
            CaseLifecycleAuditEvidence.validateApproval Int64.MaxValue row.CaseId row

            do!
                witness.ReconcileAuthority(
                    row.ApprovalId,
                    row.WitnessSequence,
                    row.WitnessEpoch,
                    row.WitnessHash,
                    row.Canonical,
                    ct
                )
        }

    let reconcile
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        (eventId: Guid)
        (ct: CancellationToken)
        =
        if eventId = Guid.Empty then
            System.Threading.Tasks.Task.FromResult(
                LifecycleReconcileOutcome.IntegrityMismatch eventId
            )
        else
            task {
                try
                    OwnerConnection.requireIdentity connection
                    RuntimeSchema.requireCompatible connection
                    do! witness.Admit(ct)
                    use! _authorityLease = AuthorityOperationFence.acquireShared None connection ct
                    use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)

                    use readOnly =
                        new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)

                    let! _ = readOnly.ExecuteNonQueryAsync(ct)
                    do! requireInstallation connection transaction witness ct
                    let! event = CaseLifecycleAuditRows.eventById connection transaction eventId ct

                    let! approval =
                        CaseLifecycleAuditRows.approvalById connection transaction eventId ct

                    match event, approval with
                    | None, None -> return LifecycleReconcileOutcome.PrimaryAbsentUnknown eventId
                    | Some _, Some _ -> return LifecycleReconcileOutcome.IntegrityMismatch eventId
                    | Some row, None ->
                        do! settleEvent connection transaction witness row ct
                        return LifecycleReconcileOutcome.Settled eventId
                    | None, Some row ->
                        do! settleApproval witness row ct
                        return LifecycleReconcileOutcome.Settled eventId
                with
                | :? InvalidDataException ->
                    return LifecycleReconcileOutcome.IntegrityMismatch eventId
                | _ -> return LifecycleReconcileOutcome.Unconfirmed eventId
            }

    let private idsPage (connection: NpgsqlConnection) after limit (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT id FROM (SELECT event_id AS id FROM claimcore.case_lifecycle_events "
                    + "UNION SELECT approval_id AS id FROM claimcore.case_lifecycle_approvals) rows "
                    + "WHERE @after::uuid IS NULL OR id>@after ORDER BY id LIMIT @limit",
                    connection
                )

            Sql.optional command "after" NpgsqlDbType.Uuid after
            Sql.add command "limit" NpgsqlDbType.Integer (box (limit + 1))
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let ids = ResizeArray<Guid>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)
                reading <- found

                if found then
                    ids.Add(reader.GetGuid(0))

            return List.ofSeq ids
        }

    let reconcilePage
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        (after: Guid option)
        limit
        (ct: CancellationToken)
        =
        task {
            if limit < 1 || limit > 1000 then
                invalidArg (nameof limit) "Use a 1-1000 page."

            OwnerConnection.requireIdentity connection
            let! ids = idsPage connection after limit ct
            let selected = ids |> List.truncate limit
            let outcomes = ResizeArray<LifecycleReconcileOutcome>()

            for id in selected do
                let! outcome = reconcile connection witness id ct
                outcomes.Add(outcome)

            return
                {
                    Outcomes = List.ofSeq outcomes
                    NextAfter =
                        if ids.Length > limit then
                            selected |> List.tryLast
                        else
                            None
                }
        }
