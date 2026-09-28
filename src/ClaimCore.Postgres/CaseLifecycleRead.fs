namespace ClaimCore.Postgres

open System
open System.Data
open System.Globalization
open System.IO
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Domain

[<NoEquality; NoComparison>]
type internal LifecycleProjection =
    {
        CaseId: Guid
        Claim: Claim
        State: LifecycleState
        Sequence: int64
        EventHash: byte array
    }

type internal LifecycleStoredEvent =
    {
        DraftHash: byte array
        Canonical: byte array
        Revision: int64
        Sequence: int64
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
    }

type internal LifecycleStoredApproval =
    {
        OperationId: Guid
        CaseId: Guid
        DraftHash: byte array
        ApproverId: Guid
        GrantRevision: int64
        ApprovedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        Canonical: byte array
        CandidateHash: byte array
    }

module internal CaseLifecycleRead =
    let private date (reader: Data.Common.DbDataReader) index =
        DateOnly.ParseExact(reader.GetString(index), "yyyy-MM-dd", CultureInfo.InvariantCulture)

    let private activeHolds connection transaction caseId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT hold_id,ground,review_on::text,recorded_by,recorded_at "
                    + "FROM claimcore.case_holds WHERE case_id=@caseId AND released_at IS NULL "
                    + "ORDER BY hold_id LIMIT 257",
                    connection,
                    transaction
                )

            Sql.uuid command "caseId" caseId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let holds = ResizeArray<LifecycleHold>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync()
                reading <- found

                if found then
                    holds.Add
                        {
                            Id = reader.GetGuid(0)
                            Ground = reader.GetString(1)
                            ReviewOn = date reader 2
                            RecordedBy = reader.GetGuid(3)
                            RecordedAt = reader.GetFieldValue<DateTimeOffset>(4)
                        }

            if holds.Count > 256 then
                return raise (InvalidDataException("Active hold capacity was exceeded."))
            else
                return List.ofSeq holds
        }

    let private claim connection transaction reference =
        task {
            use command = new NpgsqlCommand(Sql.selectCase, connection, transaction)
            Sql.text command "reference" reference
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! found = reader.ReadAsync()

            if not found then
                return raise (InvalidDataException("Case projection is inconsistent."))
            else
                let restored = Rows.claim reader

                if reader.Read() then
                    return raise (InvalidDataException("Duplicate case projection."))
                else
                    return restored
        }

    let lockProjection connection transaction reference =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT case_id,disposition,privacy_phase,lifecycle_sequence,"
                    + "lifecycle_event_hash FROM claimcore.cases WHERE case_reference=@reference FOR UPDATE",
                    connection,
                    transaction
                )

            Sql.text command "reference" reference
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! found = reader.ReadAsync()

            if not found then
                return None
            else
                let caseId = reader.GetGuid(0)

                let disposition =
                    reader.GetString(1)
                    |> CaseLifecycleCandidate.parseDisposition
                    |> Option.defaultWith (fun () ->
                        raise (InvalidDataException("Unknown case disposition.")))

                let privacy =
                    reader.GetString(2)
                    |> CaseLifecycleCandidate.parsePrivacy
                    |> Option.defaultWith (fun () ->
                        raise (InvalidDataException("Unknown case privacy phase.")))

                let sequence = reader.GetInt64(3)
                let eventHash = reader.GetFieldValue<byte array>(4)
                reader.Close()
                let! current = claim connection transaction reference
                let! holds = activeHolds connection transaction caseId
                let snapshot = Claim.view current

                let state =
                    CaseLifecycleDecisions.restore caseId snapshot disposition privacy holds
                    |> function
                        | Ok value -> value
                        | Error _ ->
                            raise (InvalidDataException("Case lifecycle projection is invalid."))

                return
                    Some
                        {
                            CaseId = caseId
                            Claim = current
                            State = state
                            Sequence = sequence
                            EventHash = eventHash
                        }
        }

    let eventById connection transaction eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT draft_sha256,canonical_action,business_revision,lifecycle_sequence,"
                    + "witness_sequence,witness_epoch,witness_entry_hash "
                    + "FROM claimcore.case_lifecycle_events WHERE event_id=@event",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! found = reader.ReadAsync()

            return
                if found then
                    Some
                        {
                            DraftHash = reader.GetFieldValue<byte array>(0)
                            Canonical = reader.GetFieldValue<byte array>(1)
                            Revision = reader.GetInt64(2)
                            Sequence = reader.GetInt64(3)
                            WitnessSequence = reader.GetInt64(4)
                            WitnessEpoch = reader.GetInt64(5)
                            WitnessHash = reader.GetFieldValue<byte array>(6)
                        }
                else
                    None
        }

    let approvalById connection transaction approvalId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT operation_id,case_id,event_digest,approver_actor_id,"
                    + "approver_grant_revision,approved_at,expires_at,witness_sequence,"
                    + "witness_epoch,witness_entry_hash,canonical_action,candidate_sha256 "
                    + "FROM claimcore.case_lifecycle_approvals WHERE approval_id=@approval",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! found = reader.ReadAsync()

            return
                if found then
                    Some
                        {
                            OperationId = reader.GetGuid(0)
                            CaseId = reader.GetGuid(1)
                            DraftHash = reader.GetFieldValue<byte array>(2)
                            ApproverId = reader.GetGuid(3)
                            GrantRevision = reader.GetInt64(4)
                            ApprovedAt = reader.GetFieldValue<DateTimeOffset>(5)
                            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(6)
                            WitnessSequence = reader.GetInt64(7)
                            WitnessEpoch = reader.GetInt64(8)
                            WitnessHash = reader.GetFieldValue<byte array>(9)
                            Canonical = reader.GetFieldValue<byte array>(10)
                            CandidateHash = reader.GetFieldValue<byte array>(11)
                        }
                else
                    None
        }

    let approvals connection transaction caseId (change: LifecycleChange) draftHash instant =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT a.approval_id,a.approver_actor_id,a.expires_at FROM "
                    + "claimcore.case_lifecycle_approvals a "
                    + "JOIN claimcore.actors actor ON actor.actor_id=a.approver_actor_id "
                    + "WHERE a.operation_id=@operation AND a.case_id=@caseId "
                    + "AND a.action_name=@action AND a.expected_revision=@revision "
                    + "AND a.expected_lifecycle_sequence=@sequence "
                    + "AND a.expected_lifecycle_hash=@hash AND a.event_digest=@digest "
                    + "AND a.approved_at<=@instant AND a.expires_at>@instant "
                    + "AND actor.enabled AND actor.principal_kind='HUMAN' "
                    + "AND EXISTS (SELECT 1 FROM claimcore.actor_grants g "
                    + "WHERE g.actor_id=actor.actor_id AND g.active AND g.role_name='DATA_STEWARD' "
                    + "AND (g.scope_kind='INSTALLATION' OR "
                    + "(g.scope_kind='CASE' AND g.scope_case_id=@caseId))) "
                    + "ORDER BY a.approver_actor_id LIMIT 3",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" change.EventId
            Sql.uuid command "caseId" caseId
            Sql.text command "action" (CaseLifecycleCandidate.actionName change.Action)
            Sql.integer command "revision" change.ExpectedRevision
            Sql.integer command "sequence" change.ExpectedLifecycleSequence

            Sql.add
                command
                "hash"
                NpgsqlDbType.Bytea
                (box (Convert.FromHexString change.ExpectedLifecycleHash))

            Sql.add command "digest" NpgsqlDbType.Bytea (box draftHash)
            Sql.add command "instant" NpgsqlDbType.TimestampTz (box instant)
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let collected = ResizeArray<LifecycleApprovalEvidence>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync()
                reading <- found

                if found then
                    collected.Add
                        {
                            ApprovalId = reader.GetGuid(0)
                            ApproverId = reader.GetGuid(1)
                            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(2)
                        }

            return List.ofSeq collected
        }
