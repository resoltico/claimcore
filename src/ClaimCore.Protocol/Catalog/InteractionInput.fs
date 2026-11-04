namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open Microsoft.FSharp.Reflection

[<RequireQualifiedAccess>]
type InteractionInput =
    | Target of InputTarget
    | Scope
    | ActorId
    | CaseId
    | Detail
    | Lifecycle
    | Status
    | Progress
    | CaseView
    | Order
    | Assignment
    | Due
    | ReminderState
    | ReminderDue
    | IncludeInactive
    | Mine
    | WorkChoice of BusinessField

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InteractionInput =
    let vocabularyName = "input"

    // Every semantic input has one fixed boundary key; this closed table contains no nested decisions.
    // fsharplint:disable-next-line CyclomaticComplexity
    let key =
        function
        | InteractionInput.Target target -> InputTarget.token target
        | InteractionInput.ReminderState -> "reminderState"
        | InteractionInput.ReminderDue -> "reminderDue"
        | InteractionInput.IncludeInactive -> "includeInactive"
        | InteractionInput.Mine -> "mine"
        | InteractionInput.Scope -> "scope"
        | InteractionInput.ActorId -> "actorId"
        | InteractionInput.CaseId -> "caseId"
        | InteractionInput.Detail -> "detail"
        | InteractionInput.Lifecycle -> "lifecycle"
        | InteractionInput.Status -> "status"
        | InteractionInput.Progress -> "progress"
        | InteractionInput.CaseView -> "caseView"
        | InteractionInput.Order -> "order"
        | InteractionInput.Due -> "due"
        | InteractionInput.WorkChoice field -> BusinessField.key field + "Action"
        | InteractionInput.Assignment -> "assignment"

    // This total semantic-input to message-namespace table contains no nested decisions.
    // fsharplint:disable-next-line CyclomaticComplexity
    let messageKey input part =
        match input with
        | InteractionInput.Target(InputTarget.Field field) ->
            Vocabulary.messageKey BusinessField.vocabulary field part
        | InteractionInput.Target(InputTarget.Group group) ->
            Vocabulary.messageKey RecordGroup.vocabulary group part
        | InteractionInput.Target target -> Vocabulary.messageKey InputTarget.vocabulary target part
        | InteractionInput.ReminderState
        | InteractionInput.ReminderDue
        | InteractionInput.IncludeInactive
        | InteractionInput.Mine
        | InteractionInput.WorkChoice _
        | InteractionInput.Scope
        | InteractionInput.ActorId
        | InteractionInput.CaseId
        | InteractionInput.Detail
        | InteractionInput.Lifecycle
        | InteractionInput.Status
        | InteractionInput.Progress
        | InteractionInput.CaseView
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
