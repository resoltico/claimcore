namespace ClaimCore.Domain


[<NoComparison>]
type Claim =
    internal
        {
            Facts: Registration
            Finance: FinancialPosition
            Status: CaseStatus
        }
