namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type Outcome<'success> =
    | Succeeded of 'success
    | Rejected of Refusal
    | Failed of CoreFault

module Outcome =
    let fold succeeded rejected failed =
        function
        | Outcome.Succeeded value -> succeeded value
        | Outcome.Rejected refusal -> rejected refusal
        | Outcome.Failed fault -> failed fault

    let ofResult =
        function
        | Ok value -> Outcome.Succeeded value
        | Error fault -> Outcome.Failed fault
