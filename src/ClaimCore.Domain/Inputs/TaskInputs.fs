namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type InputSource =
    | Authored
    | Binding
    | OperationIdentity
    | ExpectedRevision

module InputSource =
    let vocabulary =
        Vocabulary.ofUnion<InputSource> "inputSource" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type TaskInputId =
    | Target of InputTarget
    | Scope
    | ActorId
    | CaseId
    | Lifecycle
    | Status
    | Progress
    | CaseView
    | ResponsibleActorFilter
    | RoleFilter
    | DisclosureCaseId
    | DisclosureKind
    | DisclosureFrom
    | DisclosureUntil
    | ReminderView
    | TimelineFilter
    | Order
    | Assignment
    | Due
    | ReminderState
    | ReminderDue
    | ReminderKindFilter
    | DeadlineOutcomeFilter
    | IncludeInactive
    | SettleOnly
    | ConfirmRetirement
    | CaseDate of CaseDateFilter
    | GroupBy

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module TaskInputId =
    let vocabularyName = "input"

    // Every semantic input has one fixed boundary key; this closed table contains no nested decisions.
    let key =
        function
        | TaskInputId.Target target -> InputTarget.token target
        | TaskInputId.ReminderState -> "reminderState"
        | TaskInputId.DeadlineOutcomeFilter -> "deadlineOutcome"
        | TaskInputId.ReminderKindFilter -> "reminderKind"
        | TaskInputId.ReminderDue -> "reminderDue"
        | TaskInputId.IncludeInactive -> "includeInactive"
        | TaskInputId.SettleOnly -> "settleOnly"
        | TaskInputId.ConfirmRetirement -> "confirmRetirement"
        | TaskInputId.Scope -> "scope"
        | TaskInputId.ActorId -> "actorId"
        | TaskInputId.CaseId -> "caseId"
        | TaskInputId.Lifecycle -> "lifecycle"
        | TaskInputId.Status -> "status"
        | TaskInputId.Progress -> "progress"
        | TaskInputId.CaseView -> "caseView"
        | TaskInputId.ResponsibleActorFilter -> "responsibleActorFilter"
        | TaskInputId.RoleFilter -> "roleFilter"
        | TaskInputId.DisclosureCaseId -> "disclosureCaseId"
        | TaskInputId.DisclosureKind -> "disclosureKind"
        | TaskInputId.DisclosureFrom -> "disclosureFrom"
        | TaskInputId.DisclosureUntil -> "disclosureUntil"
        | TaskInputId.ReminderView -> "reminderView"
        | TaskInputId.TimelineFilter -> "timelineFilter"
        | TaskInputId.Order -> "order"
        | TaskInputId.Due -> "due"
        | TaskInputId.Assignment -> "assignment"
        | TaskInputId.CaseDate date -> CaseDateFilter.token date
        | TaskInputId.GroupBy -> "groupBy"

    /// Opaque semantic tokens distinguish facts and group inputs from similarly named query controls.
    let token input =
        match input with
        | TaskInputId.Target(InputTarget.Field field) -> "field:" + BusinessField.key field
        | TaskInputId.Target(InputTarget.Group group) -> "group:" + RecordGroup.token group
        | _ -> "input:" + key input

    let extras =
        [
            TaskInputId.Scope
            TaskInputId.ActorId
            TaskInputId.CaseId
            TaskInputId.Lifecycle
            TaskInputId.Status
            TaskInputId.Progress
            TaskInputId.CaseView
            TaskInputId.ResponsibleActorFilter
            TaskInputId.RoleFilter
            TaskInputId.DisclosureCaseId
            TaskInputId.DisclosureKind
            TaskInputId.DisclosureFrom
            TaskInputId.DisclosureUntil
            TaskInputId.ReminderView
            TaskInputId.TimelineFilter
            TaskInputId.Order
            TaskInputId.Assignment
            TaskInputId.Due
            TaskInputId.ReminderState
            TaskInputId.ReminderDue
            TaskInputId.ReminderKindFilter
            TaskInputId.DeadlineOutcomeFilter
            TaskInputId.IncludeInactive
            TaskInputId.SettleOnly
            TaskInputId.ConfirmRetirement
            TaskInputId.GroupBy
            yield! CaseDateFilter.all |> List.map TaskInputId.CaseDate
        ]

    let technical =
        (InputTarget.all
         |> List.choose (fun (target, _) ->
             match target with
             | InputTarget.Field _
             | InputTarget.Group _
             | InputTarget.Scope
             | InputTarget.CaseId -> None
             | _ -> Some(TaskInputId.Target target)))
        @ extras

    let all =
        (BusinessField.declarations
         |> List.map (fun field -> TaskInputId.Target(InputTarget.Field field.Field)))
        @ (Vocabulary.all RecordGroup.vocabulary
           |> List.map (InputTarget.Group >> TaskInputId.Target))
        @ technical
        |> List.distinct


/// Absence is a missing map member; an explicit null remains a distinct submitted value.
[<RequireQualifiedAccess>]
type InputValue =
    | Text of string
    | Integer of int64
    | Boolean of bool
    | Null

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module TaskCollectionId =
    let key =
        function
        | TaskCollectionId.PeopleGrants -> "peopleGrants"
        | TaskCollectionId.Parties -> "parties"

    let input =
        function
        | TaskCollectionId.PeopleGrants -> TaskInputId.Target InputTarget.Grants
        | TaskCollectionId.Parties -> TaskInputId.Target InputTarget.Parties

    let maximumRows (collection: TaskCollectionId) = collection.MaximumRows

    let ofRowInput =
        function
        | InputTarget.PartyRole
        | InputTarget.PartyName
        | InputTarget.PartyDetails -> TaskCollectionId.Parties
        | _ -> TaskCollectionId.PeopleGrants

    /// Acquisition defaults are explicit submitted values; they never bypass task admission.
    let defaultValues =
        function
        | TaskCollectionId.PeopleGrants ->
            Map.ofList
                [ TaskInputId.Scope, InputValue.Text(ScopeKind.token ScopeKind.Installation) ]
        | TaskCollectionId.Parties -> Map.empty

/// HUMAN issue targets are semantic identities, with an optional submitted collection row.
type TaskInputIssue =
    {
        Input: TaskInputId
        Row: TaskRowTarget option
        Violation: Violation
    }

module TaskInputIssue =
    let ofDomain (issue: InputIssue) =
        {
            Input =
                match issue.Target with
                | InputTarget.Scope -> TaskInputId.Scope
                | InputTarget.CaseId -> TaskInputId.CaseId
                | target -> TaskInputId.Target target
            Row = issue.Row
            Violation = issue.Violation
        }

/// An input rejection always explains at least one failed rule.
type TaskInputIssues = private TaskInputIssues of TaskInputIssue * TaskInputIssue list

module TaskInputIssues =
    let ofList =
        function
        | [] -> None
        | first :: rest -> Some(TaskInputIssues(first, rest))

    let toList (TaskInputIssues(first, rest)) = first :: rest
