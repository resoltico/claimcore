namespace ClaimCore.Postgres

open System
open System.Text.Json
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal CopyAdoptionCustody =
    {
        AdoptionEventId: Guid
        CopyId: Guid
        CaseId: Guid
        Origin: CopyAdoptionOrigin
        EventKind: string
        ProducerKind: string
        State: string
        Revision: int64
        PreviousEventHash: byte array
        CiphertextSha256: byte array
        CiphertextBytes: int64
        CapturedAt: DateTimeOffset
        RetainUntil: DateTimeOffset
        LocationCommitment: byte array
        CustodianCommitment: byte array
        SigningKeyId: Guid
        EncryptionKeyId: Guid
        OwnerApprovalId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        ValidUntil: DateTimeOffset
    }

/// The COPY_ATTESTOR signs the exact new copy event, not the original PRODUCT_EXPORT receipt.
module internal ManagedCopyAdoptionCustody =
    open ManagedCopyAdoptionDocumentCommon

    let private names =
        [
            "format"
            "adoptionEventId"
            "copyId"
            "caseId"
            "originKind"
            "exportId"
            "preFenceSequence"
            "preFenceHash"
            "eventKind"
            "producerKind"
            "state"
            "copyRevision"
            "previousEventHash"
            "ciphertextSha256"
            "ciphertextBytes"
            "capturedAt"
            "retainUntil"
            "locationCommitment"
            "custodianCommitment"
            "signingKeyId"
            "encryptionKeyId"
            "ownerApprovalId"
            "installationId"
            "lineageId"
            "epoch"
            "validUntil"
        ]

    let private decode (root: JsonElement) =
        {
            AdoptionEventId = uuid root "adoptionEventId"
            CopyId = uuid root "copyId"
            CaseId = uuid root "caseId"
            Origin = origin root
            EventKind = text root "eventKind"
            ProducerKind = text root "producerKind"
            State = text root "state"
            Revision = number root "copyRevision"
            PreviousEventHash = digest root "previousEventHash"
            CiphertextSha256 = digest root "ciphertextSha256"
            CiphertextBytes = number root "ciphertextBytes"
            CapturedAt = instant root "capturedAt"
            RetainUntil = instant root "retainUntil"
            LocationCommitment = digest root "locationCommitment"
            CustodianCommitment = digest root "custodianCommitment"
            SigningKeyId = uuid root "signingKeyId"
            EncryptionKeyId = uuid root "encryptionKeyId"
            OwnerApprovalId = uuid root "ownerApprovalId"
            InstallationId = uuid root "installationId"
            LineageId = uuid root "lineageId"
            Epoch = number root "epoch"
            ValidUntil = instant root "validUntil"
        }

    let private valid value =
        value.Epoch > 0L
        && value.CiphertextBytes > 0L
        && value.RetainUntil > value.CapturedAt
        && value.ValidUntil > value.CapturedAt
        && value.PreviousEventHash.Length = 32
        && (match value.Origin with
            | CopyAdoptionOrigin.ProductExport(exportId, _, _) ->
                exportId = value.CopyId
                && value.EventKind = "ADOPT"
                && value.ProducerKind = "PRODUCT_EXPORT"
                && value.State = "UNVERIFIED"
                && value.Revision = 2L
            | CopyAdoptionOrigin.AdoptedExternal _ ->
                value.EventKind = "REGISTER"
                && value.ProducerKind = "ADOPTED_EXTERNAL"
                && value.State = "UNKNOWN"
                && value.Revision = 1L
                && value.PreviousEventHash = Array.zeroCreate<byte> 32)

    let parse canonical =
        match parse names "claimcore-copy-adoption-custody-1" canonical decode with
        | Some value when valid value -> Some value
        | _ -> None
