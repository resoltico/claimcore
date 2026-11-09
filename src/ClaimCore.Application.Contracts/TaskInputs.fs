namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type InputSource =
    | Authored
    | OperationIdentity
    | ExpectedRevision

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
    | ReminderView
    | TimelineFilter
    | Order
    | Assignment
    | Due
    | ReminderState
    | ReminderDue
    | IncludeInactive
    | ResponsibilityChange

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module TaskInputId =
    let vocabularyName = "input"

    // Every semantic input has one fixed boundary key; this closed table contains no nested decisions.
    let key =
        function
        | TaskInputId.Target target -> InputTarget.token target
        | TaskInputId.ReminderState -> "reminderState"
        | TaskInputId.ReminderDue -> "reminderDue"
        | TaskInputId.IncludeInactive -> "includeInactive"
        | TaskInputId.Scope -> "scope"
        | TaskInputId.ActorId -> "actorId"
        | TaskInputId.CaseId -> "caseId"
        | TaskInputId.Lifecycle -> "lifecycle"
        | TaskInputId.Status -> "status"
        | TaskInputId.Progress -> "progress"
        | TaskInputId.CaseView -> "caseView"
        | TaskInputId.ResponsibleActorFilter -> "responsibleActorFilter"
        | TaskInputId.RoleFilter -> "roleFilter"
        | TaskInputId.ReminderView -> "reminderView"
        | TaskInputId.TimelineFilter -> "timelineFilter"
        | TaskInputId.Order -> "order"
        | TaskInputId.Due -> "due"
        | TaskInputId.ResponsibilityChange ->
            BusinessField.key BusinessField.ResponsibleActor + "Action"
        | TaskInputId.Assignment -> "assignment"

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
            TaskInputId.ReminderView
            TaskInputId.TimelineFilter
            TaskInputId.Order
            TaskInputId.Assignment
            TaskInputId.Due
            TaskInputId.ReminderState
            TaskInputId.ReminderDue
            TaskInputId.IncludeInactive
        ]

    let technical =
        (InputTarget.all
         |> List.choose (fun (target, _) ->
             match target with
             | InputTarget.Field _
             | InputTarget.Group _ -> None
             | _ -> Some(TaskInputId.Target target)))
        @ extras
        @ [ TaskInputId.ResponsibilityChange ]

    let all =
        (BusinessField.declarations
         |> List.map (fun field -> TaskInputId.Target(InputTarget.Field field.Field)))
        @ (Vocabulary.all RecordGroup.vocabulary
           |> List.map (InputTarget.Group >> TaskInputId.Target))
        @ technical
        |> List.distinct


/// Collection identity is independent of transport members and UI-local row keys.
[<RequireQualifiedAccess>]
type TaskCollectionId = | AuthorityGrants

module TaskCollectionId =
    let key =
        function
        | TaskCollectionId.AuthorityGrants -> "authorityGrants"

    let maximumRows =
        function
        | TaskCollectionId.AuthorityGrants -> AuthorityAdmission.MaximumGrants

/// Submitted ordinals correlate responses with an immutable client-owned row map.
type TaskRowTarget =
    private
        {
            Collection: TaskCollectionId
            Ordinal: int
        }

module TaskRowTarget =
    let create collection ordinal =
        if ordinal < 0 || ordinal >= TaskCollectionId.maximumRows collection then
            None
        else
            Some
                {
                    Collection = collection
                    Ordinal = ordinal
                }

    let collection target = target.Collection
    let ordinal target = target.Ordinal

/// HUMAN issue targets are semantic identities, with an optional submitted collection row.
type TaskInputIssue =
    {
        Input: TaskInputId
        Row: TaskRowTarget option
        Violation: Violation
        RelatedDate: RelatedDate option
    }

module TaskInputIssue =
    let ofDomain (issue: InputIssue) =
        {
            Input = TaskInputId.Target issue.Target
            Row =
                issue.GrantRow
                |> Option.bind (
                    GrantOrdinal.value >> TaskRowTarget.create TaskCollectionId.AuthorityGrants
                )
            Violation = issue.Violation
            RelatedDate = issue.RelatedDate
        }
