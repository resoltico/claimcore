namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading
open Npgsql
open ClaimCore.Application

/// Owner process only. Live deletion remains ERASURE_PENDING until independent managed-copy and
/// witness-payload absence plus the explicit suppression horizon are proven later.
module internal CaseErasurePurge =
    let private now connection transaction =
        task {
            use command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction)
            let! value = command.ExecuteScalarAsync()

            return
                match value with
                | :? DateTimeOffset as instant -> instant
                | :? DateTime as instant when instant.Kind = DateTimeKind.Utc ->
                    DateTimeOffset instant
                | _ -> raise (InvalidDataException("Owner purge clock is unavailable."))
        }

    let private lockAuthority connection transaction =
        task {
            use authority =
                new NpgsqlCommand(
                    "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR UPDATE",
                    connection,
                    transaction
                )

            let! value = authority.ExecuteScalarAsync()

            if not (value :? int64) then
                raise (InvalidDataException("Owner purge authority barrier is unavailable."))

            use copies =
                new NpgsqlCommand(
                    "LOCK TABLE claimcore.managed_copies,claimcore.managed_copy_events,"
                    + "claimcore.recovery_artifact_exports,claimcore.recovery_artifact_payloads "
                    + "IN SHARE ROW EXCLUSIVE MODE",
                    connection,
                    transaction
                )

            let! _ = copies.ExecuteNonQueryAsync()
            return ()
        }

    let private pending
        ownerConnectionString
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (inventory: IManagedCopyErasureClearance)
        (canonicalDraft: byte array)
        (ct: CancellationToken)
        (caseId: Guid)
        (change: LifecycleChange)
        (stored: StoredErasurePurge)
        (intentAttempted: bool ref)
        (stage: string ref)
        =
        task {
            stage.Value <- "PRE_AUDIT"

            let! tip =
                CaseErasurePurgePreparation.audit ownerConnectionString witness commitments ct

            stage.Value <- "CASE_PROJECTION"

            let! found =
                CaseLifecycleStoreSupport.lockCase
                    owner
                    transaction
                    change.CaseReference
                    change.EventId

            match found with
            | None -> return OwnerPurgeOutcome.AuditUnavailable "CASE_PROJECTION"
            | Some projection when projection.CaseId <> caseId ->
                return OwnerPurgeOutcome.AuditUnavailable "CASE_IDENTITY"
            | Some projection ->
                let! instant = now owner transaction
                stage.Value <- "PURGE_PROOF"

                return!
                    CaseErasurePurgeCommit.fresh
                        owner
                        transaction
                        witness
                        commitments
                        inventory
                        projection
                        change
                        stored
                        canonicalDraft
                        tip
                        instant
                        intentAttempted
                        stage
                        ct
        }

    let private replay
        owner
        transaction
        witness
        commitments
        canonicalDraft
        (change: LifecycleChange)
        (value: StoredErasurePurge)
        (intentAttempted: bool ref)
        (stage: string ref)
        =
        task {
            stage.Value <- "EXACT_REPLAY"
            intentAttempted.Value <- true

            let! exact =
                CaseErasurePurgeRead.reconcile
                    owner
                    transaction
                    witness
                    commitments
                    value
                    change.EventId
                    canonicalDraft

            return
                if exact then
                    OwnerPurgeOutcome.Purged(change.EventId, 0L)
                else
                    OwnerPurgeOutcome.AuditUnavailable "EXACT_REPLAY"
        }

    let private handleStored
        ownerConnectionString
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (inventory: IManagedCopyErasureClearance)
        (canonicalDraft: byte array)
        (ct: CancellationToken)
        (caseId: Guid)
        (change: LifecycleChange)
        (stored: StoredErasurePurge option)
        (intentAttempted: bool ref)
        (stage: string ref)
        =
        task {
            match stored with
            | Some(value: StoredErasurePurge) when value.EventId.IsSome ->
                return!
                    replay
                        owner
                        transaction
                        witness
                        commitments
                        canonicalDraft
                        change
                        value
                        intentAttempted
                        stage
            | Some(value: StoredErasurePurge) when value.Phase = "ERASURE_REQUESTED" ->
                return!
                    pending
                        ownerConnectionString
                        owner
                        transaction
                        witness
                        commitments
                        inventory
                        canonicalDraft
                        ct
                        caseId
                        change
                        value
                        intentAttempted
                        stage
            | _ -> return OwnerPurgeOutcome.AuditUnavailable "TOMBSTONE_STATE"
        }

    let private attempt
        ownerConnectionString
        (owner: NpgsqlConnection)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (inventory: IManagedCopyErasureClearance)
        (canonicalDraft: byte array)
        (ct: CancellationToken)
        (caseId: Guid)
        (change: LifecycleChange)
        =
        task {
            let intentAttempted = ref false
            let stage = ref "OWNER_ADMISSION"

            try
                OwnerConnection.requireIdentity owner
                SchemaBaseline.requireCurrent owner
                DatabaseEnvironment.requireCompatible owner
                witness.Admit()
                commitments.Admit()
                use! _authorityFence = AuthorityOperationFence.acquireExclusive None owner ct
                use transaction = owner.BeginTransaction(IsolationLevel.ReadCommitted)
                do! lockAuthority owner transaction
                let! stored = CaseErasurePurgeRead.find owner transaction caseId

                return!
                    handleStored
                        ownerConnectionString
                        owner
                        transaction
                        witness
                        commitments
                        inventory
                        canonicalDraft
                        ct
                        caseId
                        change
                        stored
                        intentAttempted
                        stage
            with
            | CaseIdentityCoverageUnknowable -> return OwnerPurgeOutcome.IdentityCoverageUnknowable
            | _ when intentAttempted.Value -> return OwnerPurgeOutcome.Unconfirmed change.EventId
            | _ -> return OwnerPurgeOutcome.AuditUnavailable stage.Value
        }

    let execute
        (ownerConnectionString: string)
        (owner: NpgsqlConnection)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (inventory: IManagedCopyErasureClearance)
        (canonicalDraft: byte array)
        (ct: CancellationToken)
        =
        task {
            if canonicalDraft.Length < 1 || canonicalDraft.Length > 16384 then
                return OwnerPurgeOutcome.AuditUnavailable "INPUT"
            else
                let caseId, change = CaseLifecycleAuditCodec.decodeDraft canonicalDraft

                match change.Action with
                | LifecycleMutation.PurgeLivePayload(_, validUntil) when
                    Sql.isUtcMicrosecond validUntil
                    ->
                    return!
                        attempt
                            ownerConnectionString
                            owner
                            witness
                            commitments
                            inventory
                            canonicalDraft
                            ct
                            caseId
                            change
                | LifecycleMutation.PurgeLivePayload _ ->
                    return OwnerPurgeOutcome.Refused OwnerPurgeRefusal.ProposalMismatch
                | _ -> return OwnerPurgeOutcome.Refused OwnerPurgeRefusal.ErasureNotPending
        }
