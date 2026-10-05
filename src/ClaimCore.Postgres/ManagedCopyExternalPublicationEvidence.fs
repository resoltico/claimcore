namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

/// Historical signed publication is pre-fence knowledge of one known unmanaged copy, never
/// proof of future location absence or independent proof of its claimed capture instant.
[<NoEquality; NoComparison>]
type internal VerifiedExternalCopyPublication =
    {
        PublicationId: Guid
        CopyId: Guid
        CaseId: Guid
        EncryptionKeyId: Guid
        CiphertextSha256: byte array
        CiphertextBytes: int64
        CapturedAt: DateTimeOffset
        RetainUntil: DateTimeOffset
        LocationCommitment: byte array
        CustodianCommitment: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessEntryHash: byte array
    }

module internal ManagedCopyExternalPublicationEvidence =
    let private documents (row: StoredExternalCopyPublication) =
        let submission =
            {
                PublicationId = row.PublicationId
                Registry =
                    {
                        Canonical = row.RegistryCanonical
                        Signature = row.RegistrySignature
                    }
                Inspection =
                    {
                        Canonical = row.InspectionCanonical
                        Signature = row.InspectionSignature
                    }
            }

        let parsed =
            ManagedCopyExternalPublicationDocuments.parse submission
            |> Option.defaultWith corrupt

        submission, parsed

    let private rowMatches
        (witness: WitnessProtocol)
        (row: StoredExternalCopyPublication)
        (registered: ExternalCopyPublication)
        (observed: ExternalCopyInspection)
        =
        row.CopyId = registered.CopyId
        && row.CaseId = registered.CaseId
        && row.InstallationId = registered.InstallationId
        && row.LineageId = registered.LineageId
        && row.Epoch = registered.Epoch
        && row.InstallationId = witness.Identity.InstallationId
        && row.LineageId = witness.Identity.LineageId
        && row.Epoch = witness.Identity.Epoch
        && row.EncryptionKeyId = registered.EncryptionKeyId
        && row.CiphertextSha256 = registered.CiphertextSha256
        && row.CiphertextBytes = registered.CiphertextBytes
        && row.CapturedAt = registered.CapturedAt
        && row.RetainUntil = registered.RetainUntil
        && row.LocationCommitment = registered.LocationCommitment
        && row.CustodianCommitment = registered.CustodianCommitment
        && row.RegistryKeyId = registered.RegistrySigningKeyId
        && row.InspectorKeyId = observed.InspectorSigningKeyId

    let private timeMatches
        (row: StoredExternalCopyPublication)
        (registered: ExternalCopyPublication)
        (observed: ExternalCopyInspection)
        cutoff
        =
        row.WitnessSequence <= cutoff
        && row.ActorAuthorityRevision > 0L
        && row.CaseRevision > 0L
        && row.CapturedAt <= row.PublishedAt
        && row.RetainUntil > row.PublishedAt
        && registered.IssuedAt <= row.PublishedAt
        && registered.ValidUntil = row.ValidUntil
        && row.ValidUntil > row.PublishedAt
        && row.ValidUntil <= registered.IssuedAt.AddMinutes(10.0)
        && observed.ObservedAt >= registered.IssuedAt
        && observed.ObservedAt <= row.PublishedAt
        && observed.ValidUntil > row.PublishedAt
        && observed.ValidUntil <= observed.ObservedAt.AddMinutes(5.0)

    let private signer connection transaction key purpose canonical signature sequence ct =
        task {
            let! value =
                DataAuditCopyAdoptionSignerRole.read connection transaction key purpose sequence ct

            if not (ManagedCopySignature.verify value.PublicKey canonical signature) then
                corrupt ()

            return value
        }

    let private signers connection transaction (row: StoredExternalCopyPublication) ct =
        task {
            let! registry =
                signer
                    connection
                    transaction
                    row.RegistryKeyId
                    "LOCATION_REGISTRY"
                    row.RegistryCanonical
                    row.RegistrySignature
                    row.WitnessSequence
                    ct

            let! inspector =
                signer
                    connection
                    transaction
                    row.InspectorKeyId
                    "LOCATION_INSPECTOR"
                    row.InspectionCanonical
                    row.InspectionSignature
                    row.WitnessSequence
                    ct

            if
                row.RegistryKeyId = row.InspectorKeyId
                || registry.Holder = inspector.Holder
                || row.RegistryHolderId <> registry.Holder
                || row.InspectorHolderId <> inspector.Holder
            then
                corrupt ()

            do!
                DataAuditExternalPublicationHolder.verify
                    connection
                    transaction
                    registry.Holder
                    row.CaseId
                    row.ActorAuthorityRevision
                    ct

            do!
                DataAuditExternalPublicationHolder.verify
                    connection
                    transaction
                    inspector.Holder
                    row.CaseId
                    row.ActorAuthorityRevision
                    ct
        }

    let private canonical (row: StoredExternalCopyPublication) submission registered observed =
        let expiry =
            ManagedCopyExternalPublicationCodec.privateExpiry row.Canonical
            |> Option.defaultWith corrupt

        let value =
            {
                Registry = registered
                Inspection = observed
                Submission = submission
                RegistryHolderId = row.RegistryHolderId
                InspectorHolderId = row.InspectorHolderId
                ActorAuthorityRevision = row.ActorAuthorityRevision
                CaseRevision = row.CaseRevision
                ObservedAt = row.PublishedAt
                PrivateLocationExpiresAt = expiry
            }

        let expected = ManagedCopyExternalPublicationCandidate.encode value

        try
            if
                row.ExecutorKind <> "SCHEMA_OWNER_PROCESS"
                || expected <> row.Canonical
                || row.CandidateHash <> SHA256.HashData(expected)
                || expiry <= row.PublishedAt
            then
                corrupt ()
        finally
            CryptographicOperations.ZeroMemory(expected)

    let private projected (row: StoredExternalCopyPublication) =
        {
            PublicationId = row.PublicationId
            CopyId = row.CopyId
            CaseId = row.CaseId
            EncryptionKeyId = row.EncryptionKeyId
            CiphertextSha256 = Array.copy row.CiphertextSha256
            CiphertextBytes = row.CiphertextBytes
            CapturedAt = row.CapturedAt
            RetainUntil = row.RetainUntil
            LocationCommitment = Array.copy row.LocationCommitment
            CustodianCommitment = Array.copy row.CustodianCommitment
            WitnessSequence = row.WitnessSequence
            WitnessEpoch = row.Epoch
            WitnessEntryHash = Array.copy row.WitnessHash
        }

    let verifyRow
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (row: StoredExternalCopyPublication)
        ct
        =
        task {
            let submission, (registered, observed) = documents row

            if
                not (rowMatches witness row registered observed)
                || not (timeMatches row registered observed cutoff)
                || observed.RegistryCanonicalSha256 <> SHA256.HashData(row.RegistryCanonical)
            then
                corrupt ()

            canonical row submission registered observed
            do! signers connection transaction row ct
            do! ManagedCopyExternalPublicationCaseLink.verify connection transaction row

            do!
                CaseWitnessAuditEvidence.verify
                    connection
                    transaction
                    witness
                    cutoff
                    row.CaseId
                    row.PublicationId
                    row.WitnessSequence
                    row.Epoch
                    row.WitnessHash
                    row.CandidateHash
                    SettledAuthority
                    ct

            return projected row
        }

    let verifyPublication connection transaction witness cutoff publicationId ct =
        task {
            let! found =
                ManagedCopyExternalPublicationRead.byPublication
                    connection
                    transaction
                    publicationId

            match found with
            | None -> return None
            | Some row ->
                let! proof = verifyRow connection transaction witness cutoff row ct
                return Some proof
        }

    let verifyCopy connection transaction witness cutoff copyId ct =
        task {
            let! found = ManagedCopyExternalPublicationRead.byCopy connection transaction copyId

            match found with
            | None -> return None
            | Some row ->
                let! proof = verifyRow connection transaction witness cutoff row ct
                return Some proof
        }
