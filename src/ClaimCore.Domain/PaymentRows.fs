namespace ClaimCore.Domain

/// Private payment content becomes unavailable after parent erasure; accepting provenance remains.
[<RequireQualifiedAccess>]
type PaymentProjectionContent =
    | Available of PaymentEntry * removed: bool
    | Cleared of removed: bool

type PaymentProjectionRow =
    {
        CaseId: CaseId
        PaymentId: OperationId
        Position: EffectPosition
        Content: PaymentProjectionContent
    }
