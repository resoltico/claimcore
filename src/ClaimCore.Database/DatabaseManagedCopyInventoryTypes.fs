namespace ClaimCore.Database

open System

[<NoEquality; NoComparison>]
type internal CopyLocationEntry =
    {
        CopyId: Guid
        ProducerKind: string
        CustodianId: string option
        Location: string option
        Kind: string
        SourceCaseId: Guid option
        CiphertextSha256: byte array
        CiphertextBytes: int64
    }

[<NoEquality; NoComparison>]
type internal CopyObservation =
    {
        CopyId: Guid
        Status: string
        Sha256: byte array option
        Bytes: int64 option
    }

[<NoEquality; NoComparison>]
type internal SignedCopyLocationEvidence =
    {
        RegistryKeyId: Guid
        InspectorKeyId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        CutoffSequence: int64
        CutoffHash: byte array
        RegistrySha256: byte array
        RegistryCanonical: byte array
        RegistrySignature: byte array
        InspectionCanonical: byte array
        InspectionSignature: byte array
        RegistryEntries: CopyLocationEntry list
        KnownUnmanaged: Guid list
        Observations: CopyObservation list
        ObservedAt: DateTimeOffset
        RegistryExpiresAt: DateTimeOffset
        InspectionExpiresAt: DateTimeOffset
    }
