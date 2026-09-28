namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open System.Data.Common
open ClaimCore.Application
open WitnessProtocolReconciliation

/// Rechecks one witnessed, unused actor approval under the owner transition lock.
module internal ManagedCopyDeletionEvidenceRead =
    [<NoEquality; NoComparison>]
    type TransitionBinding =
        {
            EventId: Guid
            CopyId: Guid
            SourceCaseId: Guid option
            ApprovalId: Guid option
            WitnessCutoffSequence: int64
            WitnessCutoffHash: byte array
        }

    let private query =
        "SELECT a.deletion_event_id,a.copy_id,a.verifier_signing_key_id,"
        + "a.expected_copy_revision,a.location_commitment,a.inspection_report_sha256,"
        + "a.witness_cutoff_sequence,a.witness_cutoff_hash,a.approver_actor_id,"
        + "a.approver_grant_revision,a.expires_at,a.canonical_action,a.candidate_sha256,"
        + "a.witness_sequence,a.witness_epoch,a.witness_entry_hash,"
        + "actor.enabled,actor.principal_kind,"
        + "EXISTS(SELECT 1 FROM claimcore.actor_grants g WHERE g.actor_id=a.approver_actor_id "
        + "AND g.active AND g.scope_kind='INSTALLATION' "
        + "AND g.role_name IN ('AUDITOR_CUSTODIAN','DATA_STEWARD')) "
        + "FROM claimcore.managed_copy_deletion_approvals a "
        + "JOIN claimcore.actors actor ON actor.actor_id=a.approver_actor_id "
        + "WHERE a.approval_id=@approval AND NOT EXISTS "
        + "(SELECT 1 FROM claimcore.managed_copy_deletion_approval_uses u "
        + "WHERE u.approval_id=a.approval_id)"

    let private bytes (reader: DbDataReader) index = reader.GetFieldValue<byte array>(index)

    let private request approvalId (reader: DbDataReader) : CopyDeletionApprovalRequest =
        {
            ApprovalId = approvalId
            DeletionEventId = reader.GetGuid(0)
            CopyId = reader.GetGuid(1)
            VerifierSigningKeyId = reader.GetGuid(2)
            ExpectedCopyRevision = reader.GetInt64(3)
            LocationCommitment = bytes reader 4
            InspectionReportSha256 = bytes reader 5
            WitnessCutoffSequence = reader.GetInt64(6)
            WitnessCutoffHash = bytes reader 7
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(10)
        }

    let private exact
        (transition: TransitionBinding)
        (absence: VerifiedManagedCopyDeletionAbsence)
        currentRevision
        (copyLocation: byte array)
        copyHolderActorId
        now
        (reader: DbDataReader)
        (approval: CopyDeletionApprovalRequest)
        canonical
        =
        approval.DeletionEventId = transition.EventId
        && approval.CopyId = transition.CopyId
        && approval.VerifierSigningKeyId = absence.VerifierSigningKeyId
        && approval.ExpectedCopyRevision = currentRevision
        && approval.LocationCommitment = copyLocation
        && approval.InspectionReportSha256 = absence.InspectionReportSha256
        && approval.WitnessCutoffSequence = transition.WitnessCutoffSequence
        && approval.WitnessCutoffHash = transition.WitnessCutoffHash
        && approval.ExpiresAt > now
        && reader.GetGuid(8) = absence.VerifierHolderActorId
        && reader.GetGuid(8) <> absence.RegistryHolderActorId
        && reader.GetGuid(8) <> copyHolderActorId
        && reader.GetBoolean(16)
        && reader.GetString(17) = "HUMAN"
        && reader.GetBoolean(18)
        && bytes reader 11 = canonical
        && bytes reader 12 = SHA256.HashData(canonical)

    let private witnessedApproval
        (witness: WitnessProtocol)
        (transition: TransitionBinding)
        approvalId
        (reader: DbDataReader)
        (approval: CopyDeletionApprovalRequest)
        =
        try
            match transition.SourceCaseId with
            | Some caseId ->
                witness.VerifyAuthorityEvidenceForCase(
                    approvalId,
                    reader.GetInt64(13),
                    reader.GetInt64(14),
                    bytes reader 15,
                    bytes reader 12,
                    caseId
                )
            | None ->
                witness.VerifyAuthorityEvidenceForInstallation(
                    approvalId,
                    reader.GetInt64(13),
                    reader.GetInt64(14),
                    bytes reader 15,
                    bytes reader 12
                )

            Some approval.ExpiresAt
        with _ ->
            None

    let verifyBinding
        (connection: NpgsqlConnection)
        transaction
        (witness: WitnessProtocol)
        (transition: TransitionBinding)
        (absence: VerifiedManagedCopyDeletionAbsence)
        currentRevision
        copyLocation
        copyHolderActorId
        now
        =
        task {
            match transition.ApprovalId with
            | None -> return None
            | Some approvalId ->
                use command = new NpgsqlCommand(query, connection, transaction)
                Sql.uuid command "approval" approvalId
                use! reader = command.ExecuteReaderAsync()

                if not (reader.Read()) then
                    return None
                else
                    let approval = request approvalId reader

                    let canonical =
                        ManagedCopyDeletionApprovalCandidate.canonical
                            approval
                            (reader.GetGuid(8))
                            (reader.GetInt64(9))

                    try
                        if
                            not (
                                exact
                                    transition
                                    absence
                                    currentRevision
                                    copyLocation
                                    copyHolderActorId
                                    now
                                    reader
                                    approval
                                    canonical
                            )
                            || reader.GetInt64(13) <= approval.WitnessCutoffSequence
                            || reader.GetInt64(14) <> witness.Identity.Epoch
                        then
                            return None
                        else
                            return witnessedApproval witness transition approvalId reader approval
                    finally
                        CryptographicOperations.ZeroMemory(canonical)
        }

    let verify
        connection
        transaction
        witness
        (transition: ManagedCopyTransition)
        absence
        currentRevision
        copyLocation
        copyHolderActorId
        now
        =
        verifyBinding
            connection
            transaction
            witness
            {
                EventId = transition.Copy.EventId
                CopyId = transition.Copy.CopyId
                SourceCaseId = transition.Copy.SourceCaseId
                ApprovalId = transition.DeletionApprovalId
                WitnessCutoffSequence = transition.ActionWitnessCutoffSequence
                WitnessCutoffHash = transition.ActionWitnessCutoffHash
            }
            absence
            currentRevision
            copyLocation
            copyHolderActorId
            now
