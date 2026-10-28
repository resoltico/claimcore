namespace ClaimCore.Domain

open System

type CaseSummaryFacts =
    {
        Status: CaseStatus
        ClaimantName: PartyName
        InsurerName: PartyName
        Claimed: Money
        Payable: Money option
        Progress: CaseProgress
    }

type CaseSummary =
    {
        Reference: CaseReference
        Revision: Revision
        ChangedAt: DateTimeOffset
        State: CaseState<CaseSummaryFacts>
    }

type CaseSummaryPage =
    {
        Items: CaseSummary list
        Offers: InstallationOffer list
        NextCursor: string option
    }
