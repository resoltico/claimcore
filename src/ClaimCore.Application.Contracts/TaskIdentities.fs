namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type ActSubject =
    | Case
    | People
    | Work
    | Reminder
    | Note

/// The complete act vocabulary combines the disjoint case and people subjects.
[<RequireQualifiedAccess>]
type ActKind =
    | Case of CaseActKind
    | People of PeopleActKind
    | Work of WorkActKind
    | Reminder of ReminderActKind
    | Note of NoteActKind

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActSubject =
    let vocabulary =
        Vocabulary.ofUnion<ActSubject> "subject" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActKind =
    let subject =
        function
        | ActKind.Case _ -> ActSubject.Case
        | ActKind.People _ -> ActSubject.People
        | ActKind.Work _ -> ActSubject.Work
        | ActKind.Reminder _ -> ActSubject.Reminder
        | ActKind.Note _ -> ActSubject.Note

    let vocabulary =
        [
            yield!
                Vocabulary.entries CaseActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Case value, token)
            yield!
                Vocabulary.entries PeopleActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.People value, token)
            yield!
                Vocabulary.entries WorkActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Work value, token)
            yield!
                Vocabulary.entries NoteActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Note value, token)
            yield!
                Vocabulary.entries ReminderActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Reminder value, token)
        ]
        |> Vocabulary.ofEntries "task"

    let ofInput =
        function
        | OperationInput.Case value -> ActKind.Case(CaseAct.kind value.Act)
        | OperationInput.Note value -> ActKind.Note(AuthoredNoteAct.kind value.Act)
        | OperationInput.Work _ -> ActKind.Work WorkActKind.UpdateWork
        | OperationInput.Reminder value -> ActKind.Reminder(AuthoredReminderAct.kind value.Act)
        | OperationInput.People value -> ActKind.People(PeopleAct.kind value.Act)

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

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
type CommandTaskId = | PeopleRequestsDismiss

module CommandTaskId =
    let all = [ CommandTaskId.PeopleRequestsDismiss ]

    let key =
        function
        | CommandTaskId.PeopleRequestsDismiss -> "people.requests.dismiss"

[<RequireQualifiedAccess>]
type TaskId =
    | Operation of ActKind
    | Query of QueryTaskId
    | Command of CommandTaskId

module TaskId =
    let ofOperation input = TaskId.Operation(ActKind.ofInput input)

    let key =
        function
        | TaskId.Operation act -> ActKind.token act
        | TaskId.Query query -> QueryTaskId.key query
        | TaskId.Command command -> CommandTaskId.key command

    let all =
        (ActKind.all
         |> List.filter ((<>) (ActKind.People PeopleActKind.ProvisionOwner))
         |> List.map TaskId.Operation)
        @ (QueryTaskId.all |> List.map TaskId.Query)
        @ (CommandTaskId.all |> List.map TaskId.Command)
