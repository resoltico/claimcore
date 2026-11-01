namespace ClaimCore.Domain


[<NoComparison>]
type Claim =
    internal
        {
            Work: CaseWork
            Facts: Registration
            Progress: PaymentProgress
            Status: CaseStatus
        }
