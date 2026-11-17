namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type QueryTaskId =
    | CaseGet
    | CaseAssignees
    | CaseList
    | CaseSummary
    | CaseTimeline
    | CaseNotes
    | CasePayments
    | ReminderList
    | ReminderGet
    | WorkViews
    | OperationGet
    | PeopleList
    | PeopleGrants
    | PeopleHistory
    | PeopleRequests
    | Context

module QueryTaskId =
    let vocabulary =
        Vocabulary.ofEntries
            "queryTask"
            [
                QueryTaskId.CaseGet, "case.get"
                QueryTaskId.CaseAssignees, "case.assignees"
                QueryTaskId.CaseList, "case.list"
                QueryTaskId.CaseSummary, "case.summary"
                QueryTaskId.CaseTimeline, "case.timeline"
                QueryTaskId.CaseNotes, "case.notes"
                QueryTaskId.CasePayments, "case.payments"
                QueryTaskId.ReminderList, "reminder.list"
                QueryTaskId.ReminderGet, "reminder.get"
                QueryTaskId.WorkViews, "work.views"
                QueryTaskId.OperationGet, "operation.get"
                QueryTaskId.PeopleList, "people.list"
                QueryTaskId.PeopleGrants, "people.grants"
                QueryTaskId.PeopleHistory, "people.history"
                QueryTaskId.PeopleRequests, "people.requests"
                QueryTaskId.Context, "context"
            ]

    let all = Vocabulary.all vocabulary
    let key = Vocabulary.token vocabulary


[<RequireQualifiedAccess>]
type CommandTaskId =
    | DismissAccessRequest
    | TerminateOwnSessions
    | TerminatePersonSessions

module CommandTaskId =
    let private cases =
        Vocabulary.ofUnion<CommandTaskId> "commandTaskCase" TokenCasing.CamelCase None
        |> Vocabulary.all

    let vocabulary =
        Vocabulary.define "commandTask" cases (function
            | CommandTaskId.DismissAccessRequest -> "people.requests.dismiss"
            | CommandTaskId.TerminateOwnSessions -> "session.terminateOwn"
            | CommandTaskId.TerminatePersonSessions -> "people.terminateSessions")

    let all = Vocabulary.all vocabulary
    let key = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

/// Operational requests are complete at the control boundary; they have no ledger identity.
[<RequireQualifiedAccess>]
type CommandRequest =
    | DismissAccessRequest of PrincipalKey
    | TerminateOwnSessions
    | TerminatePersonSessions of ActorId

module CommandRequest =
    let task =
        function
        | CommandRequest.DismissAccessRequest _ -> CommandTaskId.DismissAccessRequest
        | CommandRequest.TerminateOwnSessions -> CommandTaskId.TerminateOwnSessions
        | CommandRequest.TerminatePersonSessions _ -> CommandTaskId.TerminatePersonSessions
