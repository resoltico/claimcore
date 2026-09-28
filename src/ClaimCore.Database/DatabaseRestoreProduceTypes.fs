namespace ClaimCore.Database

open System

[<NoEquality; NoComparison>]
type internal RestoreProduceInput =
    {
        Publication: TrustedRestorePublication
        Index: RestoreEvidenceIndex
        ReportSigner: byte array -> byte array
        CustodyKeyId: Guid
        BackupCaptureSequence: int64
        BackupCaptureHash: string
        ValidUntil: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal SignedRestoreProduction =
    {
        Evidence: RestoreProducedEvidence
        ReportSignature: byte array
    }
