namespace ClaimCore.Application

open System

/// Provenance is a closed choice. An external copy needs a separately published pre-fence
/// registry checkpoint; a product export uses its immutable witnessed export receipt.
[<RequireQualifiedAccess>]
type CopyAdoptionOrigin =
    | ProductExport of exportId: Guid * receiptSequence: int64 * receiptHash: byte array
    | AdoptedExternal of registrySequence: int64 * registryHash: byte array

/// A human OWNER approves one exact metadata-only adoption draft. It neither asserts raw
/// location bytes nor certifies deletion; the schema-owner later re-verifies three signed
/// documents and consumes this witnessed approval under the authority/copy lock.
[<NoEquality; NoComparison>]
type CopyAdoptionApprovalRequest =
    {
        ApprovalId: Guid
        AdoptionEventId: Guid
        CopyId: Guid
        CaseId: Guid
        Origin: CopyAdoptionOrigin
        CiphertextSha256: byte array
        CiphertextBytes: int64
        CapturedAt: DateTimeOffset
        RetainUntil: DateTimeOffset
        LocationCommitment: byte array
        CustodianCommitment: byte array
        CustodianSigningKeyId: Guid
        RegistrySigningKeyId: Guid
        InspectorSigningKeyId: Guid
        CustodianCanonicalSha256: byte array
        RegistryCanonicalSha256: byte array
        InspectionReportSha256: byte array
        ExpiresAt: DateTimeOffset
    }

[<RequireQualifiedAccess>]
type CopyAdoptionApprovalOutcome =
    | Approved of approvalId: Guid * authorityRevision: int64
    | ResourceUnavailable
    | StartedUnconfirmed of approvalId: Guid
