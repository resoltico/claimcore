namespace ClaimCore.Domain

/// Current content access and reviewed changes use the same case-scoped capabilities.
module AttachmentAccess =
    let private unavailable = Refusal.Access AccessRefusal.ResourceUnavailable

    let read principal access (snapshot: CaseSnapshot) =
        if
            snapshot.State = CaseState.Erased
            || not (Access.can principal access Capability.ReadCase (Scope.Case snapshot.CaseId))
        then
            Error unavailable
        else
            Ok()

    let current principal access (snapshot: CaseSnapshot) latest =
        read principal access snapshot
        |> Result.bind (fun () ->
            match latest with
            | Some(AttachmentEffect.Added(_, Some descriptor)) -> Ok descriptor
            | _ -> Error unavailable)
