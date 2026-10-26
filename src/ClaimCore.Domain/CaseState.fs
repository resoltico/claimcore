namespace ClaimCore.Domain

open ClaimCore.Contract

[<NoComparison>]
type Claim =
    internal
        {
            Facts: Validation.Facts
            Progress: PaymentProgress
            Status: CaseStatus
        }
