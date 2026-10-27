namespace ClaimCore.Domain


type CaseSummary =
    {
        Reference: CaseReference
        Revision: int64
        State: CaseState<CaseStatus>
    }

type CaseSummaryPage =
    {
        Items: CaseSummary list
        Offers: InstallationOffer list
        NextCursor: string option
    }
