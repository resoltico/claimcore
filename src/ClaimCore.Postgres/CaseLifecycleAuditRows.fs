namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Threading
open Npgsql
open NpgsqlTypes

type internal LifecycleAuditEventRow =
    {
        EventId: Guid
        CaseId: Guid
        Reference: string
        Sequence: int64
        BusinessRevision: int64
        ActionName: string
        ActorId: Guid
        GrantRevision: int64
        DraftHash: byte array
        Canonical: byte array
        CandidateHash: byte array
        PreviousHash: byte array
        EventHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        ActorExists: bool
    }

type internal LifecycleAuditApprovalRow =
    {
        ApprovalId: Guid
        OperationId: Guid
        CaseId: Guid
        ActionName: string
        ExpectedRevision: int64
        ExpectedSequence: int64
        ExpectedHash: byte array
        DraftHash: byte array
        ApproverId: Guid
        GrantRevision: int64
        ApprovedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        Canonical: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        Human: bool
    }

module internal CaseLifecycleAuditRows =
    let private eventRow (reader: DbDataReader) =
        {
            EventId = reader.GetGuid(0)
            CaseId = reader.GetGuid(1)
            Reference = reader.GetString(2)
            Sequence = reader.GetInt64(3)
            BusinessRevision = reader.GetInt64(4)
            ActionName = reader.GetString(5)
            ActorId = reader.GetGuid(6)
            GrantRevision = reader.GetInt64(7)
            DraftHash = reader.GetFieldValue<byte array>(8)
            Canonical = reader.GetFieldValue<byte array>(9)
            CandidateHash = reader.GetFieldValue<byte array>(10)
            PreviousHash = reader.GetFieldValue<byte array>(11)
            EventHash = reader.GetFieldValue<byte array>(12)
            WitnessSequence = reader.GetInt64(13)
            WitnessEpoch = reader.GetInt64(14)
            WitnessHash = reader.GetFieldValue<byte array>(15)
            ActorExists = reader.GetBoolean(16)
        }

    let private eventSql whereClause =
        "SELECT e.event_id,e.case_id,e.case_reference,e.lifecycle_sequence,"
        + "e.business_revision,e.action_name,e.actor_id,e.grant_revision,e.draft_sha256,"
        + "e.canonical_action,e.candidate_sha256,e.previous_hash,e.event_hash,"
        + "e.witness_sequence,e.witness_epoch,e.witness_entry_hash,"
        + "EXISTS(SELECT 1 FROM claimcore.actors a WHERE a.actor_id=e.actor_id) "
        + "FROM claimcore.case_lifecycle_events e "
        + whereClause

    let eventsPage connection transaction caseId afterSequence limit (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    eventSql "WHERE e.case_id=@caseId AND e.lifecycle_sequence>@after "
                    + "ORDER BY e.lifecycle_sequence LIMIT @limit",
                    connection,
                    transaction
                )

            Sql.uuid command "caseId" caseId
            Sql.integer command "after" afterSequence
            Sql.add command "limit" NpgsqlDbType.Integer (box limit)
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let rows = ResizeArray<LifecycleAuditEventRow>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)
                reading <- found

                if found then
                    rows.Add(eventRow reader)

            return List.ofSeq rows
        }

    let dispositionPage connection transaction caseId afterRevision limit (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    eventSql "WHERE e.case_id=@caseId AND e.business_revision>@after "
                    + "AND e.action_name IN ('VOID_DATA_ENTRY_ERROR','REINSTATE_VOIDED') "
                    + "ORDER BY e.business_revision LIMIT @limit",
                    connection,
                    transaction
                )

            Sql.uuid command "caseId" caseId
            Sql.integer command "after" afterRevision
            Sql.add command "limit" NpgsqlDbType.Integer (box limit)
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let rows = ResizeArray<LifecycleAuditEventRow>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)
                reading <- found

                if found then
                    rows.Add(eventRow reader)

            return List.ofSeq rows
        }

    let eventById connection transaction eventId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(eventSql "WHERE e.event_id=@event", connection, transaction)

            Sql.uuid command "event" eventId
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let! found = reader.ReadAsync(ct)

            if not found then
                return None
            else
                let row = eventRow reader
                let! extra = reader.ReadAsync(ct)

                if extra then
                    invalidOp "Duplicate lifecycle event."

                return Some row
        }

    let private approvalRow (reader: DbDataReader) =
        {
            ApprovalId = reader.GetGuid(0)
            OperationId = reader.GetGuid(1)
            CaseId = reader.GetGuid(2)
            ActionName = reader.GetString(3)
            ExpectedRevision = reader.GetInt64(4)
            ExpectedSequence = reader.GetInt64(5)
            ExpectedHash = reader.GetFieldValue<byte array>(6)
            DraftHash = reader.GetFieldValue<byte array>(7)
            ApproverId = reader.GetGuid(8)
            GrantRevision = reader.GetInt64(9)
            ApprovedAt = reader.GetFieldValue<DateTimeOffset>(10)
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(11)
            Canonical = reader.GetFieldValue<byte array>(12)
            CandidateHash = reader.GetFieldValue<byte array>(13)
            WitnessSequence = reader.GetInt64(14)
            WitnessEpoch = reader.GetInt64(15)
            WitnessHash = reader.GetFieldValue<byte array>(16)
            Human = not (reader.IsDBNull(17)) && reader.GetString(17) = "HUMAN"
        }

    let private approvalSql whereClause =
        "SELECT p.approval_id,p.operation_id,p.case_id,p.action_name,"
        + "p.expected_revision,p.expected_lifecycle_sequence,p.expected_lifecycle_hash,"
        + "p.event_digest,p.approver_actor_id,p.approver_grant_revision,"
        + "p.approved_at,p.expires_at,p.canonical_action,p.candidate_sha256,"
        + "p.witness_sequence,p.witness_epoch,p.witness_entry_hash,a.principal_kind "
        + "FROM claimcore.case_lifecycle_approvals p "
        + "LEFT JOIN claimcore.actors a ON a.actor_id=p.approver_actor_id "
        + whereClause

    let approvalsPage connection transaction caseId afterId limit (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    approvalSql
                        "WHERE p.case_id=@caseId AND (@after::uuid IS NULL OR p.approval_id>@after) "
                    + "ORDER BY p.approval_id LIMIT @limit",
                    connection,
                    transaction
                )

            Sql.uuid command "caseId" caseId
            Sql.optional command "after" NpgsqlDbType.Uuid afterId
            Sql.add command "limit" NpgsqlDbType.Integer (box limit)
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let rows = ResizeArray<LifecycleAuditApprovalRow>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)
                reading <- found

                if found then
                    rows.Add(approvalRow reader)

            return List.ofSeq rows
        }

    let approvalById connection transaction approvalId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    approvalSql "WHERE p.approval_id=@approval",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let! found = reader.ReadAsync(ct)

            if not found then
                return None
            else
                let row = approvalRow reader
                let! extra = reader.ReadAsync(ct)

                if extra then
                    invalidOp "Duplicate lifecycle approval."

                return Some row
        }
