namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type ActSubject =
    | Case
    | Authority
    | Work
    | Reminder
    | Note

/// The complete act vocabulary combines the disjoint case and authority subjects.
[<RequireQualifiedAccess>]
type ActKind =
    | Case of CaseActKind
    | Authority of AuthorityKind
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
        | ActKind.Authority _ -> ActSubject.Authority
        | ActKind.Work _ -> ActSubject.Work
        | ActKind.Reminder _ -> ActSubject.Reminder
        | ActKind.Note _ -> ActSubject.Note

    let vocabulary =
        [
            yield!
                Vocabulary.entries CaseActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Case value, token)
            yield!
                Vocabulary.entries AuthorityKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Authority value, token)
            yield!
                Vocabulary.entries WorkActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Work value, token)
            yield!
                Vocabulary.entries NoteActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Note value, token)
            yield!
                Vocabulary.entries ReminderActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Reminder value, "REMINDER_" + token)
        ]
        |> Vocabulary.ofEntries "act"

    /// Action token within its subject; reminder task IDs use a separate qualified token.
    let subjectActionToken =
        function
        | ActKind.Case kind -> Vocabulary.token CaseActKind.vocabulary kind
        | ActKind.Authority kind -> Vocabulary.token AuthorityKind.vocabulary kind
        | ActKind.Work kind -> Vocabulary.token WorkActKind.vocabulary kind
        | ActKind.Reminder kind -> Vocabulary.token ReminderActKind.vocabulary kind
        | ActKind.Note kind -> Vocabulary.token NoteActKind.vocabulary kind

    let ofInput =
        function
        | OperationInput.Case value -> ActKind.Case(CaseAct.kind value.Act)
        | OperationInput.Note value -> ActKind.Note(AuthoredNoteAct.kind value.Act)
        | OperationInput.Work _ -> ActKind.Work WorkActKind.UpdateWork
        | OperationInput.Reminder value -> ActKind.Reminder(AuthoredReminderAct.kind value.Act)
        | OperationInput.Authority value -> ActKind.Authority(AuthorityAct.kind value.Act)

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
    | AuthorityList
    | AuthorityGrants
    | AuthorityHistory
    | AuthorityRequests
    | AuthorityRequestsDismiss
    | Context

module QueryTaskId =
    let vocabulary =
        Vocabulary.ofUnion<QueryTaskId> "queryTask" TokenCasing.CamelCase None

    let all = Vocabulary.all vocabulary
    let key = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type TaskId =
    | Operation of ActKind
    | Query of QueryTaskId

module TaskId =
    let ofOperation input = TaskId.Operation(ActKind.ofInput input)

    let key =
        function
        | TaskId.Operation act -> ActSubject.token (ActKind.subject act) + ":" + ActKind.token act
        | TaskId.Query query -> "query:" + QueryTaskId.key query

    let all =
        (ActKind.all
         |> List.filter ((<>) (ActKind.Authority AuthorityKind.ProvisionOwner))
         |> List.map TaskId.Operation)
        @ (QueryTaskId.all |> List.map TaskId.Query)
