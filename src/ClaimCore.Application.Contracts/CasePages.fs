namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

open System

type CaseSummaryFacts =
    {
        Work: CaseWorkView
        Status: CaseStatus
        ClaimantName: PartyName
        InsurerName: PartyName
        Claimed: Money
        Payable: Money option
        Progress: CaseProgress
        IncidentNotificationDate: CalendarDate
    }

type CaseSummary =
    {
        CaseId: CaseId
        Reference: CaseReference
        Revision: Revision
        ChangedAt: DateTimeOffset
        State: CaseState<CaseSummaryFacts>
    }

type CaseSummaryPage =
    {
        Basis: WorkDateBasis
        Items: CaseSummary list
        Offers: InstallationOffer list
        NextCursor: string option
    }

type AssigneePage =
    {
        Items: AccountSubject list
        NextCursor: string option
    }
