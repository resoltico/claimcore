namespace ClaimCore.Domain


type CaseSummary =
    {
        Reference: CaseReference
        Revision: Revision
        State: CaseState<CaseStatus>
    }

type CaseSummaryPage =
    {
        Items: CaseSummary list
        Offers: InstallationOffer list
        NextCursor: string option
    }
