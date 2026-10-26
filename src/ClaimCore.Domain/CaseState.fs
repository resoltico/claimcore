namespace ClaimCore.Domain


[<NoComparison>]
type Claim =
    internal
        {
            Facts: Validation.Facts
            Progress: PaymentProgress
            Status: CaseStatus
        }
