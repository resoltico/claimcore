namespace ClaimCore.Web

open ClaimCore.Contracts

/// The HTTP host obtains route locations and raw-media constraints from the same Contracts model
/// that generates browser artifacts. Endpoint handlers remain explicit, but no route path or raw
/// import media type is independently described by the host.
module WebContract =
    let private model = ContractProjection.current ()

    let private endpoint identifier =
        model.WebEndpoints
        |> List.tryFind (fun value -> value.Identifier = identifier)
        |> Option.defaultWith (fun () ->
            invalidOp $"The Web contract has no '{identifier}' endpoint.")

    let path identifier =
        endpoint identifier |> fun value -> value.Path

    let jsonPath identifier =
        match endpoint identifier with
        | { Body = Some(JsonBody _); Path = path } -> path
        | _ -> invalidOp $"The Web contract endpoint '{identifier}' is not JSON."

    let raw identifier =
        match endpoint identifier with
        | {
              Body = Some(RawBody(mediaType, maximumBytes, requiredHeaders))
              Path = path
          } -> path, mediaType, maximumBytes, requiredHeaders |> List.map fst
        | _ -> invalidOp $"The Web contract endpoint '{identifier}' is not a raw artifact endpoint."
