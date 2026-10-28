namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Every read authorizes and decodes its continuation inside one store snapshot.
module internal QueryResults =
    let fault =
        function
        | StoreFailure.Internal -> CoreFault.InternalError
        | StoreFailure.Unavailable -> CoreFault.StoreUnavailable
        | StoreFailure.SchemaMismatch -> CoreFault.SchemaMismatch
        | _ -> CoreFault.StoreIntegrityError

    let failure =
        function
        | StoreFailure.Cancelled -> Outcome.Failed CoreFault.StoreUnavailable
        | value -> Outcome.Failed(fault value)

    let unavailable<'Value> : Outcome<'Value> =
        Outcome.Rejected Refusal.ResourceUnavailable

    let validLimit limit =
        limit >= 1 && limit <= Paging.MaximumPageSize

    let invalidLimit () =
        Outcome.Rejected(
            Refusal.invalidInput (
                InputTarget.Limit,
                Violation.PageLimitRange Paging.MaximumPageSize
            )
        )

    let parseReference value =
        CaseReference.parse value
        |> Result.mapError (fun violation ->
            Refusal.invalidInput (InputTarget.Field BusinessField.CaseReference, violation))

    let currentActor principal actor =
        if Authority.validActor principal actor then
            Ok actor
        else
            Error Refusal.ResourceUnavailable

    let readResult result = result |> Result.defaultWith failure

    let succeeded result = Outcome.ofResult result

    let collect results =
        List.foldBack
            (fun item rest ->
                Result.bind (fun value -> Result.map (fun rest -> value :: rest) rest) item)
            results
            (Ok [])

    let cursor decode =
        function
        | None -> Ok None
        | Some value -> decode value |> Result.map Some
