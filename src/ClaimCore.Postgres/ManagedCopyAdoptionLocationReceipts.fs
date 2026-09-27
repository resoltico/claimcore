namespace ClaimCore.Postgres

open System
open System.Text.Json

[<NoEquality; NoComparison>]
type internal CopyAdoptionRegistryReceipt =
    {
        AdoptionEventId: Guid
        CopyId: Guid
        CaseId: Guid
        LocationCommitment: byte array
        CustodianCommitment: byte array
        CustodianCanonicalSha256: byte array
        SigningKeyId: Guid
        ObservedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal CopyAdoptionInspectionReceipt =
    {
        AdoptionEventId: Guid
        CopyId: Guid
        CaseId: Guid
        LocationCommitment: byte array
        RegistryCanonicalSha256: byte array
        CiphertextSha256: byte array
        CiphertextBytes: int64
        SigningKeyId: Guid
        ObservedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

/// A signed, metadata-only current custody statement. The actual private path remains in the
/// owner inventory and must match the HMAC commitment under the private key at admission.
module internal ManagedCopyAdoptionRegistryReceipt =
    open ManagedCopyAdoptionDocumentCommon

    let private names =
        [
            "format"
            "adoptionEventId"
            "copyId"
            "caseId"
            "locationCommitment"
            "custodianCommitment"
            "custodianCanonicalSha256"
            "signingKeyId"
            "observedAt"
            "validUntil"
        ]

    let private decode (root: JsonElement) =
        {
            AdoptionEventId = uuid root "adoptionEventId"
            CopyId = uuid root "copyId"
            CaseId = uuid root "caseId"
            LocationCommitment = digest root "locationCommitment"
            CustodianCommitment = digest root "custodianCommitment"
            CustodianCanonicalSha256 = digest root "custodianCanonicalSha256"
            SigningKeyId = uuid root "signingKeyId"
            ObservedAt = instant root "observedAt"
            ValidUntil = instant root "validUntil"
        }

    let parse canonical =
        match parse names "claimcore-copy-adoption-registry-1" canonical decode with
        | Some value when value.ValidUntil > value.ObservedAt -> Some value
        | _ -> None

/// Independent LOCATION_INSPECTOR observation must positively match present bytes at adoption.
/// UNKNOWN, missing and ABSENT observations are not a custody adoption proof.
module internal ManagedCopyAdoptionInspectionReceipt =
    open ManagedCopyAdoptionDocumentCommon

    let private names =
        [
            "format"
            "adoptionEventId"
            "copyId"
            "caseId"
            "status"
            "locationCommitment"
            "registryCanonicalSha256"
            "ciphertextSha256"
            "ciphertextBytes"
            "signingKeyId"
            "observedAt"
            "validUntil"
        ]

    let private decode (root: JsonElement) =
        if text root "status" <> "PRESENT" then
            invalidOp "Copy adoption inspection is not present."

        {
            AdoptionEventId = uuid root "adoptionEventId"
            CopyId = uuid root "copyId"
            CaseId = uuid root "caseId"
            LocationCommitment = digest root "locationCommitment"
            RegistryCanonicalSha256 = digest root "registryCanonicalSha256"
            CiphertextSha256 = digest root "ciphertextSha256"
            CiphertextBytes = number root "ciphertextBytes"
            SigningKeyId = uuid root "signingKeyId"
            ObservedAt = instant root "observedAt"
            ValidUntil = instant root "validUntil"
        }

    let parse canonical =
        match parse names "claimcore-copy-adoption-inspection-1" canonical decode with
        | Some value when value.CiphertextBytes > 0L && value.ValidUntil > value.ObservedAt ->
            Some value
        | _ -> None
