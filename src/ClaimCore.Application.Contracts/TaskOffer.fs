namespace ClaimCore.Application.Contracts

open ClaimCore.Domain
open ClaimCore.Domain.Tasks

/// DATA reports task identities. A current server snapshot additionally supplies executable controls.
[<RequireQualifiedAccess>]
type TaskOffer =
    | Declared of TaskId
    | Available of TaskId * Binding * Availability

module TaskOffer =
    let id =
        function
        | TaskOffer.Declared id
        | TaskOffer.Available(id, _, _) -> id
