namespace ClaimCore.Protocol

open ClaimCore.Domain
open Microsoft.FSharp.Reflection

[<RequireQualifiedAccess>]
type InteractionInput =
    | Target of InputTarget
    | Scope
    | ActorId
    | Detail
    | Lifecycle
    | Status
    | Progress
    | Order

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InteractionInput =
    let vocabularyName = "input"

    let key =
        function
        | InteractionInput.Target target -> InputTarget.token target
        | InteractionInput.Scope -> "scope"
        | InteractionInput.ActorId -> "actorId"
        | InteractionInput.Detail -> "detail"
        | InteractionInput.Lifecycle -> "lifecycle"
        | InteractionInput.Status -> "status"
        | InteractionInput.Progress -> "progress"
        | InteractionInput.Order -> "order"

    let messageKey input part =
        match input with
        | InteractionInput.Target(InputTarget.Field field) ->
            Vocabulary.messageKey BusinessField.vocabulary field part
        | InteractionInput.Target(InputTarget.Group group) ->
            Vocabulary.messageKey RecordGroup.vocabulary group part
        | InteractionInput.Target target -> Vocabulary.messageKey InputTarget.vocabulary target part
        | InteractionInput.Scope
        | InteractionInput.ActorId
        | InteractionInput.Detail
        | InteractionInput.Lifecycle
        | InteractionInput.Status
        | InteractionInput.Progress
        | InteractionInput.Order -> vocabularyName + "." + key input + "." + part

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
