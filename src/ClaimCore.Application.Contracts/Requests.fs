namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

/// Authored references and retained IDs are explicitly distinguished, including UUID-shaped references.
[<RequireQualifiedAccess>]
type CaseSelector =
    | Reference of string
    | Id of CaseId

/// Read/review scopes publish the resolved retained identity, independently of authored selectors.
[<RequireQualifiedAccess>]
type GrantScope =
    | Installation
    | Case of CaseId * CaseReference

type ScopedGrant = Grant<GrantScope>

type CaseOperationRequest =
    {
        OperationId: Guid
        Target: CaseSelector
        ExpectedRevision: int64
        Act: AuthoredCaseAct
    }

type PageRequest =
    {
        AfterCursor: string option
        Limit: int
    }

type HistoryRequest =
    {
        Target: CaseSelector
        AfterCursor: string option
        Limit: int
        Detail: HistoryDetail
    }

type GrantsRequest =
    {
        ActorId: Guid
        AfterCursor: string option
        Limit: int
    }

type AuthorityHistoryRequest =
    {
        TargetActorId: Guid option
        AfterCursor: string option
        Limit: int
    }

type CaseGetRequest = { Target: CaseSelector }

type OperationLookup = { OperationId: Guid }


type CaseListRequest =
    {
        Page: PageRequest
        Filter: CaseListFilter<string>
    }

type AssigneeRequest =
    {
        Target: CaseSelector
        AfterCursor: string option
        Limit: int
    }


type WorkOperationRequest =
    {
        OperationId: Guid
        Target: CaseSelector
        ExpectedRevision: int64
        Act: AuthoredWorkAct
    }

type ReminderOperationRequest =
    {
        OperationId: Guid
        Target: CaseSelector
        Act: AuthoredReminderAct
    }

type ReminderListRequest =
    {
        Target: CaseSelector option
        State: ReminderState option
        Due: ReminderDue option
        Assignment: CaseAssignment option
        Text: string option
        AfterCursor: string option
        Limit: int
        IncludeInactive: bool
    }

type WorkOverviewRequest = { Mine: bool }

type CaseActivityRequest =
    {
        Target: CaseSelector
        AfterCursor: string option
        Limit: int
        Detail: HistoryDetail
    }
