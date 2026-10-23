namespace ClaimCore.Contract

/// Correlation checks the two echoes the caller relies on; authority remains in Application.
module Correlation =
    let rec private at path value =
        match path with
        | [] -> Some value
        | name :: rest -> JsonValue.tryMember name value |> Option.bind (at rest)

    let private same expected actual =
        match expected, actual with
        | Some(JsonValue.String left), Some(JsonValue.String right) -> left = right
        | _ -> false

    let matches endpoint input reply =
        match Api.id endpoint with
        | "operation.preview"
        | "operation.execute" -> same (at [ "operationId" ] input) (at [ "operationId" ] reply)
        | "authority.grants" when at [ "outcome"; "tag" ] reply = Some(JsonValue.String "SUCCEEDED") ->
            same (at [ "actorId" ] input) (at [ "outcome"; "data"; "actor"; "actorId" ] reply)
        | _ -> true
