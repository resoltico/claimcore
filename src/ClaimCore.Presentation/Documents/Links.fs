namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// A query constructor owns both its endpoint and its typed input.
[<RequireQualifiedAccess>]
type Link =
    | Context
    | CaseGet of CaseGetRequest
    | CaseAssignees of AssigneeRequest
    | CaseList of CaseListRequest
    | CaseSummary of CaseListFilter<string>
    | CaseTimeline of CaseTimelineRequest
    | CaseNotes of CaseNotesRequest
    | CasePayments of CasePaymentsRequest
    | ReminderList of ReminderListRequest
    | ReminderGet of ReminderGetRequest
    | WorkViews
    | OperationGet of OperationLookup
    | OperationLookupForm
    | PeopleList of ActorListRequest
    | PeopleGrants of GrantsRequest
    | PeopleHistory of PeopleHistoryRequest
    | PeopleRequests of PageRequest

module Link =
    let endpoint =
        function
        | Link.Context -> QueryTaskId.Context
        | Link.CaseGet _ -> QueryTaskId.CaseGet
        | Link.CaseAssignees _ -> QueryTaskId.CaseAssignees
        | Link.CaseList _ -> QueryTaskId.CaseList
        | Link.CaseSummary _ -> QueryTaskId.CaseSummary
        | Link.CaseTimeline _ -> QueryTaskId.CaseTimeline
        | Link.CaseNotes _ -> QueryTaskId.CaseNotes
        | Link.CasePayments _ -> QueryTaskId.CasePayments
        | Link.ReminderList _ -> QueryTaskId.ReminderList
        | Link.ReminderGet _ -> QueryTaskId.ReminderGet
        | Link.WorkViews -> QueryTaskId.WorkViews
        | Link.OperationGet _
        | Link.OperationLookupForm -> QueryTaskId.OperationGet
        | Link.PeopleList _ -> QueryTaskId.PeopleList
        | Link.PeopleGrants _ -> QueryTaskId.PeopleGrants
        | Link.PeopleHistory _ -> QueryTaskId.PeopleHistory
        | Link.PeopleRequests _ -> QueryTaskId.PeopleRequests

    let caseById id =
        Link.CaseGet { Target = CaseSelector.Id id }

    let person id =
        Link.PeopleGrants
            {
                ActorId = ActorId.value id
                AfterCursor = None
                Limit = Paging.DefaultPageSize
            }

[<RequireQualifiedAccess>]
type LinkRelation =
    | OpenCase
    | FollowUp
    | View

module LinkRelation =
    let vocabulary =
        Vocabulary.ofUnion<LinkRelation> "linkRelation" TokenCasing.ScreamingSnake None

type NamedLink =
    {
        Relation: LinkRelation
        LabelId: string
        Link: Link
    }
