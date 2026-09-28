namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

/// Exact immutable copy binding; a physical proof is not an installation recovery report.
module internal ManagedCopyPhysicalVerificationPolicy =
    let private samePhysical (copy: ManagedCopyAttestation) (proof: ManagedCopyPhysicalProof) =
        proof.Cluster = copy.Cluster
        && proof.Kind = copy.Kind
        && copy.PostgresSystemId = Some proof.PostgresSystemId
        && copy.Timeline = Some proof.Timeline
        && copy.WalSegmentBytes = Some proof.WalSegmentBytes
        && proof.BackupManifestSha256 = copy.BackupManifestSha256
        && proof.WalStartLsn = copy.WalStartLsn
        && proof.WalEndLsn = copy.WalEndLsn
        && proof.WalSegment = copy.WalSegment
        && proof.LocationCommitment = copy.LocationCommitment
        && proof.CiphertextSha256 = copy.CiphertextSha256
        && proof.CiphertextBytes = copy.CiphertextBytes

    let matches
        (witness: WitnessProtocol)
        (transition: ManagedCopyTransition)
        (current: ManagedCopyCurrent)
        (original: ManagedCopyAttestation)
        (verified: VerifiedManagedCopyRestore)
        now
        =
        let proof = verified.Proof

        transition.EventKind = "VERIFY"
        && transition.State = "RETAINED"
        && current.State = "UNVERIFIED"
        && current.Revision = 1L
        && transition.Revision = 2L
        && transition.PreviousEventHash = current.EventHash
        && ManagedCopyTransitionPolicy.sameCopy original transition.Copy
        && proof.VerificationEventId = transition.Copy.EventId
        && proof.CopyId = transition.Copy.CopyId
        && proof.CopyEventId = original.EventId
        && proof.CopyRevision = transition.Revision
        && proof.InstallationId = witness.Identity.InstallationId
        && proof.LineageId = witness.Identity.LineageId
        && proof.Epoch = witness.Identity.Epoch
        && proof.WitnessCutoffSequence = transition.ActionWitnessCutoffSequence
        && proof.WitnessCutoffHash = transition.ActionWitnessCutoffHash
        && proof.VerifierSigningKeyId <> original.SigningKeyId
        && transition.Copy.VerificationProofSha256 = Some verified.ReportSha256
        && transition.LastVerifiedAt = Some proof.CheckedAt
        && transition.DeletionProofSha256.IsNone
        && transition.DeletionApprovalId.IsNone
        && proof.CheckedAt >= original.CapturedAt
        && proof.CheckedAt <= now
        && proof.ValidUntil > now
        && current.RetainUntil > now
        && samePhysical original proof

    let private activeHolder (connection: NpgsqlConnection) transaction keyId expectedPurpose =
        use command =
            new NpgsqlCommand(
                "SELECT s.ed25519_public_key,s.active,s.signer_purpose,s.holder_actor_id,"
                + "a.principal_kind,a.enabled,"
                + "EXISTS (SELECT 1 FROM claimcore.actor_grants g "
                + "WHERE g.actor_id=s.holder_actor_id AND g.active "
                + "AND g.scope_kind='INSTALLATION' "
                + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000'::uuid "
                + "AND g.role_name IN ('AUDITOR_CUSTODIAN','DATA_STEWARD')) "
                + "FROM claimcore.managed_copy_signers s "
                + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
                + "WHERE s.signing_key_id=@key FOR SHARE OF s,a",
                connection,
                transaction
            )

        Sql.uuid command "key" keyId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            None
        else
            let publicKey = reader.GetFieldValue<byte array>(0)
            let active = reader.GetBoolean(1)
            let purpose = reader.GetString(2)
            let holder = reader.GetGuid(3)
            let human = reader.GetString(4) = "HUMAN"
            let enabled = reader.GetBoolean(5)
            let granted = reader.GetBoolean(6)
            let one = not (reader.Read())

            if
                one
                && publicKey.Length = 32
                && active
                && human
                && enabled
                && granted
                && purpose = expectedPurpose
            then
                Some(publicKey, holder)
            else
                None

    let signer
        connection
        transaction
        (proof: ManagedCopyPhysicalProof)
        (canonical: byte array)
        (signature: byte array)
        copySignerKeyId
        =
        match
            activeHolder connection transaction proof.VerifierSigningKeyId "RESTORE_COPY_VERIFIER",
            activeHolder connection transaction copySignerKeyId "COPY_ATTESTOR"
        with
        | Some(publicKey, verifierHolder), Some(_, copyHolder) ->
            verifierHolder = proof.VerifierHolderActorId
            && verifierHolder <> copyHolder
            && signature.Length = 64
            && ManagedCopySignature.verify publicKey canonical signature
        | _ -> false
