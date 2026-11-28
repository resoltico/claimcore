namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

open System

type CaseSummaryFacts =
    {
        Parties: Map<PartyRole, PartyName list>
    }

type CaseReportGroupingFacts =
    {
        RegisteredOn: CalendarDate
        ReceivedOn: CalendarDate option
        ClosedOn: CalendarDate option
        DaysOpen: int
        Outcome: ClosureOutcome option
    }

type CaseCurrencyContribution =
    {
        Currency: Currency
        Claimed: DecimalTotal option
        Payable: DecimalTotal option
        NetPaid: DecimalTotal option
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
        WorkRevision: Revision
        CanAssign: bool
        ChangedAt: DateTimeOffset
        State: CaseState<CaseSummaryFacts>
        Headline: CaseHeadline
        Attention: CaseAttention list
        Stages: CaseStageOutline option
        NextAction: CaseNextAction option
        Responsible: ActorRef option
        PendingReminders: int64
        Contributions: CaseCurrencyContribution list
        Grouping: CaseReportGroupingFacts
        PaymentCount: int64
        LatestTransferDate: CalendarDate option
    }

type CaseRef =
    {
        CaseId: CaseId
        CaseReference: CaseReference
    }

type ExactCaseReference =
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
        ExactReference: ExactCaseReference option
        AuthorizedScopeEmpty: bool
    }

type AssigneePage =
    {
        CaseId: CaseId
        WorkRevision: Revision
        Basis: WorkDateBasis
        Items: ActorRef list
        NextCursor: string option
    }

type PartyChoice =
    {
        PartyId: PartyId
        Name: PartyName option
        Role: PartyRole
    }

type PartyChoicePage =
    {
        Basis: WorkDateBasis
        Items: PartyChoice list
        NextCursor: string option
    }
