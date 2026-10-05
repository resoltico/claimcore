namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon
open WitnessProtocolReconciliation

/// Replays each retained physical-copy report in both directions against witnessed VERIFY.
/// Historical signer/grant proof is separate from current copy availability.
module internal DataAuditCopyPhysicalVerifications =
    let private originalMatches (row: CopyPhysicalAuditRow) (original: ManagedCopyAttestation) =
        let proof = row.Proof

        original.CopyId = row.CopyId
        && original.EventId = proof.CopyEventId
        && original.InstallationId = proof.InstallationId
        && original.LineageId = proof.LineageId
        && original.Epoch = proof.Epoch
        && original.Cluster = proof.Cluster
        && original.Kind = proof.Kind
        && original.PostgresSystemId = Some proof.PostgresSystemId
        && original.Timeline = Some proof.Timeline
        && original.WalSegmentBytes = Some proof.WalSegmentBytes
        && original.BackupManifestSha256 = proof.BackupManifestSha256
        && original.WalStartLsn = proof.WalStartLsn
        && original.WalEndLsn = proof.WalEndLsn
        && original.WalSegment = proof.WalSegment
        && original.LocationCommitment = proof.LocationCommitment
        && original.CiphertextSha256 = proof.CiphertextSha256
        && original.CiphertextBytes = proof.CiphertextBytes

    let private eventMatches
        (witness: WitnessProtocol)
        cutoff
        (row: CopyPhysicalAuditRow)
        (transition: ManagedCopyTransition)
        =
        row.EventKind = "VERIFY"
        && row.ProducerKind = "OWNER_ATTESTED"
        && transition.EventKind = "VERIFY"
        && transition.State = "RETAINED"
        && transition.Copy.EventId = row.EventId
        && transition.Copy.CopyId = row.CopyId
        && transition.Revision = row.Revision
        && transition.Copy.VerificationProofSha256 = Some row.ReportSha256
        && transition.LastVerifiedAt = Some row.Proof.CheckedAt
        && transition.ActionWitnessCutoffSequence = row.Proof.WitnessCutoffSequence
        && transition.ActionWitnessCutoffHash = row.Proof.WitnessCutoffHash
        && row.EventWitnessSequence > row.Proof.WitnessCutoffSequence
        && row.EventWitnessSequence <= cutoff
        && row.EventWitnessEpoch = witness.Identity.Epoch
        && row.Proof.InstallationId = witness.Identity.InstallationId
        && row.Proof.LineageId = witness.Identity.LineageId
        && row.Proof.Epoch = witness.Identity.Epoch

    let private signerMatches (row: CopyPhysicalAuditRow) =
        row.SignerPurpose = "RESTORE_COPY_VERIFIER"
        && row.SignerHolder = row.Proof.VerifierHolderActorId
        && row.SignerHolder <> row.CopyAttestorHolder
        && row.PublicKey.Length = 32
        && row.SignerRegisteredSequence < row.EventWitnessSequence
        && (row.SignerRetiredSequence
            |> Option.forall (fun sequence -> sequence > row.EventWitnessSequence))
        && ManagedCopySignature.verify row.PublicKey row.Canonical row.Signature

    let private projectionMatches (row: CopyPhysicalAuditRow) =
        row.CopyRevision >= row.Revision
        && row.CopyProofSha256 = Some row.ReportSha256
        && (match row.CopyState, row.CopyLastVerifiedAt with
            | "RETAINED", Some instant -> instant = row.Proof.CheckedAt
            | "DELETE_PENDING", Some instant
            | "VERIFIED_DELETED", Some instant
            | "UNKNOWN", Some instant -> instant >= row.Proof.CheckedAt
            | _ -> false)

    let private verifyCheckpoints (witness: WitnessProtocol) (row: CopyPhysicalAuditRow) ct =
        witnessProofAsync (fun () ->
            task {
                for sequence, hash in
                    [
                        row.Proof.WitnessCutoffSequence, row.Proof.WitnessCutoffHash
                        row.EventWitnessSequence, row.EventWitnessHash
                    ] do
                    do! witness.VerifyHistoricalTip(sequence, hash, ct)
            })

    let private verifyRow
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (row: CopyPhysicalAuditRow)
        (ct: CancellationToken)
        =
        task {
            let original =
                ManagedCopyRegistrationAttestation.parse row.OriginalCanonical
                |> Option.defaultWith corrupt

            let transition =
                ManagedCopyTransitionAttestation.parse row.EventCanonical
                |> Option.defaultWith corrupt

            if
                not row.MetadataMatches
                || not (originalMatches row original)
                || not (eventMatches witness cutoff row transition)
                || not (signerMatches row)
                || not (projectionMatches row)
                || row.ReportSha256 <> SHA256.HashData(row.Canonical)
                || row.Proof.CheckedAt > row.EventRecordedAt
                || row.Proof.ValidUntil <= row.EventRecordedAt
                || row.Proof.CheckedAt < original.CapturedAt
                || row.EventRecordedAt >= original.RetainUntil
            then
                corrupt ()

            do! verifyCheckpoints witness row ct

            do!
                DataAuditCopyPhysicalVerifierRole.verify
                    connection
                    transaction
                    row.SignerHolder
                    row.EventWitnessSequence
                    ct
        }

    let private noMissingReceipt (connection: NpgsqlConnection) transaction =
        use command =
            new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM claimcore.managed_copy_events e "
                + "WHERE e.producer_kind='OWNER_ATTESTED' AND e.event_kind='VERIFY' "
                + "AND NOT EXISTS (SELECT 1 FROM claimcore.managed_copy_verifications v "
                + "WHERE v.verification_event_id=e.event_id))",
                connection,
                transaction
            )

        if unbox<bool>(command.ExecuteScalar()) then
            corrupt ()

    let verify connection transaction witness cutoff (ct: CancellationToken) =
        task {
            let mutable after = Guid.Empty
            let mutable count = 0L
            let mutable more = true

            while more do
                let! rows = DataAuditCopyPhysicalRows.page connection transaction after ct

                for row in rows do
                    if row.EventId <= after then
                        corrupt ()

                    do! verifyRow connection transaction witness cutoff row ct
                    after <- row.EventId
                    count <- count + 1L

                more <- rows.Length = 50

            noMissingReceipt connection transaction
            return count
        }
