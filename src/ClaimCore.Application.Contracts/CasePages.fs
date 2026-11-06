namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

open System

type CaseSummaryFacts =
    {
        Fields: Map<BusinessField, CaseFieldValue option>
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
        Stages: CaseStageOutline option
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
        Basis: WorkDateBasis
        Items: ActorRef list
        NextCursor: string option
    }
