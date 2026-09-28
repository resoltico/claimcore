namespace ClaimCore.Postgres

open System
open System.Buffers.Binary
open System.Text

/// Byte-exact transition candidate and immutable copy-identity checks.
module internal ManagedCopyTransitionPolicy =
    let candidate
        (transition: ManagedCopyTransition)
        (canonical: byte array)
        (signature: byte array)
        =
        let length = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteInt32BigEndian(length, canonical.Length)

        Array.concat
            [
                Encoding.ASCII.GetBytes("CLAIMCORE_OWNER_MANAGED_COPY_TRANSITION_V1\000")
                transition.Copy.EventId.ToByteArray()
                transition.Copy.SigningKeyId.ToByteArray()
                length
                canonical
                signature
            ]

    let sameCopy (original: ManagedCopyAttestation) (next: ManagedCopyAttestation) =
        original.CopyId = next.CopyId
        && original.InstallationId = next.InstallationId
        && original.LineageId = next.LineageId
        && original.Epoch = next.Epoch
        && original.Cluster = next.Cluster
        && original.Kind = next.Kind
        && original.SourceCaseId = next.SourceCaseId
        && original.PostgresSystemId = next.PostgresSystemId
        && original.Timeline = next.Timeline
        && original.WalSegmentBytes = next.WalSegmentBytes
        && original.BackupManifestSha256 = next.BackupManifestSha256
        && original.WalStartLsn = next.WalStartLsn
        && original.WalEndLsn = next.WalEndLsn
        && original.WalSegment = next.WalSegment
        && original.WitnessCutoffSequence = next.WitnessCutoffSequence
        && original.WitnessCutoffHash = next.WitnessCutoffHash
        && original.CiphertextSha256 = next.CiphertextSha256
        && original.CiphertextBytes = next.CiphertextBytes
        && original.EncryptionKeyId = next.EncryptionKeyId
        && original.CustodianCommitment = next.CustodianCommitment
        && original.LocationCommitment = next.LocationCommitment
        && original.CapturedAt = next.CapturedAt
        && original.RetainUntil = next.RetainUntil
        && original.CycleId = next.CycleId

    let allowed (current: ManagedCopyCurrent) (transition: ManagedCopyTransition) now hasHold =
        let carry =
            transition.Copy.VerificationProofSha256 = current.VerificationProofSha256
            && transition.LastVerifiedAt = current.LastVerifiedAt

        let request =
            transition.EventKind = "DELETE_REQUEST"
            && (current.State = "UNVERIFIED" || current.State = "RETAINED")
            && now >= current.RetainUntil
            && not hasHold
            && carry

        let uncertain =
            transition.EventKind = "UNKNOWN"
            && (current.State = "UNVERIFIED"
                || current.State = "RETAINED"
                || current.State = "DELETE_PENDING")
            && carry

        request || uncertain
