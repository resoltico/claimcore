namespace ClaimCore.Postgres

open System
open System.Text.Json

/// The two detached signed documents contain only pseudonymous copy identity and keyed
/// custody commitments. The owner-private mapping retains the actual inspectable path.
module internal ManagedCopyExternalPublicationDocuments =
    open ManagedCopyAdoptionDocumentCommon

    let private registryNames =
        [
            "format"
            "publicationId"
            "copyId"
            "caseId"
            "installationId"
            "lineageId"
            "epoch"
            "encryptionKeyId"
            "ciphertextSha256"
            "ciphertextBytes"
            "capturedAt"
            "retainUntil"
            "locationCommitment"
            "custodianCommitment"
            "signingKeyId"
            "issuedAt"
            "validUntil"
        ]

    let private inspectionNames =
        [
            "format"
            "publicationId"
            "copyId"
            "caseId"
            "status"
            "ciphertextSha256"
            "ciphertextBytes"
            "locationCommitment"
            "registryCanonicalSha256"
            "signingKeyId"
            "observedAt"
            "validUntil"
        ]

    let private registry (root: JsonElement) =
        {
            PublicationId = uuid root "publicationId"
            CopyId = uuid root "copyId"
            CaseId = uuid root "caseId"
            InstallationId = uuid root "installationId"
            LineageId = uuid root "lineageId"
            Epoch = number root "epoch"
            EncryptionKeyId = uuid root "encryptionKeyId"
            CiphertextSha256 = digest root "ciphertextSha256"
            CiphertextBytes = number root "ciphertextBytes"
            CapturedAt = instant root "capturedAt"
            RetainUntil = instant root "retainUntil"
            LocationCommitment = digest root "locationCommitment"
            CustodianCommitment = digest root "custodianCommitment"
            RegistrySigningKeyId = uuid root "signingKeyId"
            IssuedAt = instant root "issuedAt"
            ValidUntil = instant root "validUntil"
        }

    let private inspection (root: JsonElement) =
        if text root "status" <> "PRESENT" then
            invalidOp "External copy publication inspection is not present."

        {
            PublicationId = uuid root "publicationId"
            CopyId = uuid root "copyId"
            CaseId = uuid root "caseId"
            CiphertextSha256 = digest root "ciphertextSha256"
            CiphertextBytes = number root "ciphertextBytes"
            LocationCommitment = digest root "locationCommitment"
            RegistryCanonicalSha256 = digest root "registryCanonicalSha256"
            InspectorSigningKeyId = uuid root "signingKeyId"
            ObservedAt = instant root "observedAt"
            ValidUntil = instant root "validUntil"
        }

    let private sameCopy
        (submission: ExternalCopyPublicationSubmission)
        (registered: ExternalCopyPublication)
        (observed: ExternalCopyInspection)
        =
        registered.PublicationId = submission.PublicationId
        && observed.PublicationId = submission.PublicationId
        && registered.CopyId = observed.CopyId
        && registered.CaseId = observed.CaseId
        && registered.CiphertextSha256 = observed.CiphertextSha256
        && registered.CiphertextBytes = observed.CiphertextBytes
        && registered.LocationCommitment = observed.LocationCommitment

    let private validTimes
        (registered: ExternalCopyPublication)
        (observed: ExternalCopyInspection)
        =
        registered.CiphertextBytes > 0L
        && registered.Epoch > 0L
        && registered.RetainUntil > registered.IssuedAt
        && registered.CapturedAt <= registered.IssuedAt
        && registered.ValidUntil > registered.IssuedAt
        && observed.ObservedAt >= registered.IssuedAt
        && observed.ValidUntil > observed.ObservedAt

    let parse (submission: ExternalCopyPublicationSubmission) =
        match
            parse
                registryNames
                "claimcore-external-copy-registry-publication-1"
                submission.Registry.Canonical
                registry,
            parse
                inspectionNames
                "claimcore-external-copy-publication-inspection-1"
                submission.Inspection.Canonical
                inspection
        with
        | Some registered, Some observed when
            sameCopy submission registered observed && validTimes registered observed
            ->
            Some(registered, observed)
        | _ -> None
