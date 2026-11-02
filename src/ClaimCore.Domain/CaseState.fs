namespace ClaimCore.Domain


[<NoComparison>]
type Claim =
    internal
        {
            Facts: Registration
            Progress: PaymentProgress
            Status: CaseStatus
        }
