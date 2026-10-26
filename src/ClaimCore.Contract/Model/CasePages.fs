namespace ClaimCore.Contract

type CaseSummary =
    {
        Reference: string
        Revision: int64
        State: CaseState<CaseStatus>
    }

type CaseSummaryPage =
    {
        Items: CaseSummary list
        Offers: InstallationOffer list
        NextCursor: string option
    }
