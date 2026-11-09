namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open Microsoft.FSharp.Reflection

[<RequireQualifiedAccess>]
type InteractionInput =
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
    | WorkChoice of BusinessField

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InteractionInput =
    let vocabularyName = "input"

    // Every semantic input has one fixed boundary key; this closed table contains no nested decisions.
    let key =
        function
        | InteractionInput.Target target -> InputTarget.token target
        | InteractionInput.ReminderState -> "reminderState"
        | InteractionInput.ReminderDue -> "reminderDue"
        | InteractionInput.IncludeInactive -> "includeInactive"
        | InteractionInput.Scope -> "scope"
        | InteractionInput.ActorId -> "actorId"
        | InteractionInput.CaseId -> "caseId"
        | InteractionInput.Lifecycle -> "lifecycle"
        | InteractionInput.Status -> "status"
        | InteractionInput.Progress -> "progress"
        | InteractionInput.CaseView -> "caseView"
        | InteractionInput.ResponsibleActorFilter -> "responsibleActorFilter"
        | InteractionInput.RoleFilter -> "roleFilter"
        | InteractionInput.ReminderView -> "reminderView"
        | InteractionInput.TimelineFilter -> "timelineFilter"
        | InteractionInput.Order -> "order"
        | InteractionInput.Due -> "due"
        | InteractionInput.WorkChoice field -> BusinessField.key field + "Action"
        | InteractionInput.Assignment -> "assignment"

    // This total semantic-input to message-namespace table contains no nested decisions.
    let messageKey input part =
        match input with
        | InteractionInput.Target(InputTarget.Field field) ->
            if part = "rule" then
                FieldPresentation.ruleId field
            else
                Vocabulary.messageKey BusinessField.vocabulary field part
        | InteractionInput.Target(InputTarget.Reason | InputTarget.Text | InputTarget.ProfileDisplayName | InputTarget.ProfileContact) when
            part = "rule"
            ->
            "scalar.text.guidance"
        | InteractionInput.Target(InputTarget.Group group) ->
            Vocabulary.messageKey RecordGroup.vocabulary group part
        | InteractionInput.Target target -> Vocabulary.messageKey InputTarget.vocabulary target part
        | InteractionInput.ReminderState
        | InteractionInput.ReminderDue
        | InteractionInput.IncludeInactive
        | InteractionInput.WorkChoice _
        | InteractionInput.Scope
        | InteractionInput.ActorId
        | InteractionInput.CaseId
        | InteractionInput.Lifecycle
        | InteractionInput.Status
        | InteractionInput.Progress
        | InteractionInput.CaseView
        | InteractionInput.ResponsibleActorFilter
        | InteractionInput.RoleFilter
        | InteractionInput.ReminderView
        | InteractionInput.TimelineFilter
        | InteractionInput.Order
        | InteractionInput.Due
        | InteractionInput.Assignment -> vocabularyName + "." + key input + "." + part

    let extras =
        FSharpType.GetUnionCases typeof<InteractionInput>
        |> Array.filter (fun case -> case.GetFields().Length = 0)
        |> Array.map (fun case ->
            (FSharpValue.MakeUnion(case, [||]) |> nonNull) :?> InteractionInput)
        |> Array.toList

    let technical =
        (InputTarget.all
         |> List.choose (fun (target, _) ->
             match target with
             | InputTarget.Field _
             | InputTarget.Group _ -> None
             | _ -> Some(InteractionInput.Target target)))
        @ extras
        @ [ InteractionInput.WorkChoice BusinessField.ResponsibleActor ]

    let hintId input =
        match input with
        | InteractionInput.Lifecycle
        | InteractionInput.Progress
        | InteractionInput.CaseView
        | InteractionInput.ResponsibleActorFilter
        | InteractionInput.ReminderView
        | InteractionInput.Assignment
        | InteractionInput.Due
        | InteractionInput.ReminderState
        | InteractionInput.ReminderDue
        | InteractionInput.IncludeInactive -> None
        | _ -> Some(messageKey input "hint")
