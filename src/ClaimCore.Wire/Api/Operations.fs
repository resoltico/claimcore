namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type OperationEndpoint =
    | Preview
    | Execute

[<RequireQualifiedAccess>]
type ActorEndpoint =
    | Operation of OperationEndpoint
    | Query of QueryTaskId

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActorEndpoint =
    // This total endpoint-token table owns stable public spellings and has no nested decisions; every closed endpoint remains compiler-checked.
    let vocabulary =
        Vocabulary.define
            "endpoint"
            [
                (ActorEndpoint.Operation OperationEndpoint.Preview)
                (ActorEndpoint.Operation OperationEndpoint.Execute)
                (ActorEndpoint.Query QueryTaskId.CaseAssignees)
                (ActorEndpoint.Query QueryTaskId.CaseGet)
                (ActorEndpoint.Query QueryTaskId.CaseList)

                (ActorEndpoint.Query QueryTaskId.CaseSummary)

                (ActorEndpoint.Query QueryTaskId.CaseNotes)
                (ActorEndpoint.Query QueryTaskId.CaseTimeline)
                (ActorEndpoint.Query QueryTaskId.ReminderList)
                (ActorEndpoint.Query QueryTaskId.ReminderGet)

                (ActorEndpoint.Query QueryTaskId.WorkViews)
                (ActorEndpoint.Query QueryTaskId.OperationGet)
                (ActorEndpoint.Query QueryTaskId.AuthorityList)
                (ActorEndpoint.Query QueryTaskId.AuthorityGrants)
                (ActorEndpoint.Query QueryTaskId.AuthorityHistory)
                (ActorEndpoint.Query QueryTaskId.AuthorityRequests)
                (ActorEndpoint.Query QueryTaskId.AuthorityRequestsDismiss)
                (ActorEndpoint.Query QueryTaskId.Context)
            ]
            (function
             | (ActorEndpoint.Operation OperationEndpoint.Preview) -> "operation.preview"
             | (ActorEndpoint.Operation OperationEndpoint.Execute) -> "operation.execute"
             | (ActorEndpoint.Query QueryTaskId.CaseAssignees) -> "case.assignees"
             | (ActorEndpoint.Query QueryTaskId.CaseGet) -> "case.get"
             | (ActorEndpoint.Query QueryTaskId.CaseList) -> "case.list"

             | (ActorEndpoint.Query QueryTaskId.CaseSummary) -> "case.summary"

             | (ActorEndpoint.Query QueryTaskId.CaseNotes) -> "case.notes"
             | (ActorEndpoint.Query QueryTaskId.CaseTimeline) -> "case.timeline"
             | (ActorEndpoint.Query QueryTaskId.ReminderList) -> "reminder.list"
             | (ActorEndpoint.Query QueryTaskId.ReminderGet) -> "reminder.get"

             | (ActorEndpoint.Query QueryTaskId.WorkViews) -> "work.views"
             | (ActorEndpoint.Query QueryTaskId.OperationGet) -> "operation.get"
             | (ActorEndpoint.Query QueryTaskId.AuthorityList) -> "authority.list"
             | (ActorEndpoint.Query QueryTaskId.AuthorityGrants) -> "authority.grants"
             | (ActorEndpoint.Query QueryTaskId.AuthorityHistory) -> "authority.history"
             | (ActorEndpoint.Query QueryTaskId.AuthorityRequests) -> "authority.requests"
             | (ActorEndpoint.Query QueryTaskId.AuthorityRequestsDismiss) ->
                 "authority.requests.dismiss"
             | (ActorEndpoint.Query QueryTaskId.Context) -> "context")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
