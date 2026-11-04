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

type CaseNextAction =
    {
        ReminderId: ReminderId
        Action: ReminderAction
        Due: CalendarDate
        DueState: CaseDue
    }

type CaseSummary =
    {
        CaseId: CaseId
        Reference: CaseReference
        Revision: Revision
        ChangedAt: DateTimeOffset
        State: CaseState<CaseSummaryFacts>
        Headline: CaseHeadline
        Attention: CaseAttention list
        NextAction: CaseNextAction option
        Responsible: ActorRef option
        PendingReminders: int64
    }

type CaseRef =
    {
        CaseId: CaseId
        CaseReference: CaseReference
    }

type CaseSummaryPage =
    {
        Basis: WorkDateBasis
        Items: CaseSummary list
        Offers: InstallationOffer list
        NextCursor: string option
        ExactReference: CaseRef option
    }

type AssigneePage =
    {
        Items: ActorRef list
        NextCursor: string option
    }
