namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type OperationEndpoint =
    | Preview
    | Execute

[<RequireQualifiedAccess>]
type QueryEndpoint =
    | CaseGet
    | CaseAssignees
    | CaseList
    | CaseSummary
    | CaseTimeline
    | ReminderList
    | ReminderGet
    | WorkViews
    | OperationGet
    | AuthorityList
    | AuthorityGrants
    | AuthorityHistory
    | AuthorityRequests
    | AuthorityRequestsDismiss
    | Context

[<RequireQualifiedAccess>]
type ActorEndpoint =
    | Operation of OperationEndpoint
    | Query of QueryEndpoint

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActorEndpoint =
    // This total endpoint-token table owns stable public spellings and has no nested decisions; every closed endpoint remains compiler-checked.
    // fsharplint:disable-next-line CyclomaticComplexity
    let vocabulary =
        Vocabulary.define
            "endpoint"
            [
                (ActorEndpoint.Operation OperationEndpoint.Preview)
                (ActorEndpoint.Operation OperationEndpoint.Execute)
                (ActorEndpoint.Query QueryEndpoint.CaseAssignees)
                (ActorEndpoint.Query QueryEndpoint.CaseGet)
                (ActorEndpoint.Query QueryEndpoint.CaseList)

                (ActorEndpoint.Query QueryEndpoint.CaseSummary)

                (ActorEndpoint.Query QueryEndpoint.CaseTimeline)
                (ActorEndpoint.Query QueryEndpoint.ReminderList)
                (ActorEndpoint.Query QueryEndpoint.ReminderGet)

                (ActorEndpoint.Query QueryEndpoint.WorkViews)
                (ActorEndpoint.Query QueryEndpoint.OperationGet)
                (ActorEndpoint.Query QueryEndpoint.AuthorityList)
                (ActorEndpoint.Query QueryEndpoint.AuthorityGrants)
                (ActorEndpoint.Query QueryEndpoint.AuthorityHistory)
                (ActorEndpoint.Query QueryEndpoint.AuthorityRequests)
                (ActorEndpoint.Query QueryEndpoint.AuthorityRequestsDismiss)
                (ActorEndpoint.Query QueryEndpoint.Context)
            ]
            (function
             | (ActorEndpoint.Operation OperationEndpoint.Preview) -> "operation.preview"
             | (ActorEndpoint.Operation OperationEndpoint.Execute) -> "operation.execute"
             | (ActorEndpoint.Query QueryEndpoint.CaseAssignees) -> "case.assignees"
             | (ActorEndpoint.Query QueryEndpoint.CaseGet) -> "case.get"
             | (ActorEndpoint.Query QueryEndpoint.CaseList) -> "case.list"

             | (ActorEndpoint.Query QueryEndpoint.CaseSummary) -> "case.summary"

             | (ActorEndpoint.Query QueryEndpoint.CaseTimeline) -> "case.timeline"
             | (ActorEndpoint.Query QueryEndpoint.ReminderList) -> "reminder.list"
             | (ActorEndpoint.Query QueryEndpoint.ReminderGet) -> "reminder.get"

             | (ActorEndpoint.Query QueryEndpoint.WorkViews) -> "work.views"
             | (ActorEndpoint.Query QueryEndpoint.OperationGet) -> "operation.get"
             | (ActorEndpoint.Query QueryEndpoint.AuthorityList) -> "authority.list"
             | (ActorEndpoint.Query QueryEndpoint.AuthorityGrants) -> "authority.grants"
             | (ActorEndpoint.Query QueryEndpoint.AuthorityHistory) -> "authority.history"
             | (ActorEndpoint.Query QueryEndpoint.AuthorityRequests) -> "authority.requests"
             | (ActorEndpoint.Query QueryEndpoint.AuthorityRequestsDismiss) ->
                 "authority.requests.dismiss"
             | (ActorEndpoint.Query QueryEndpoint.Context) -> "context")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
