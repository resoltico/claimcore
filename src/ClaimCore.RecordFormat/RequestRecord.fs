namespace ClaimCore.RecordFormat

open ClaimCore.Contract
open ClaimCore.Json
open ClaimCore.Domain

module RequestRecord =
    let private codec = RecordCodecs.caseOperation |> Codec.named "CaseRequestRecord"

    let private validate request =
        match Case.validateRequest request with
        | Ok() -> Ok request
        | Error(Refusal.InvalidInput(target, _)) ->
            Error
                {
                    Field = "$." + InputTargets.token target
                    Message = "Invalid case-request format."
                }
        | Error _ ->
            Error
                {
                    Field = "$.act"
                    Message = "Invalid case-request format."
                }

    let decode maxBytes bytes =
        Codec.decode maxBytes codec bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })
        |> Result.bind validate

    let encode request =
        match validate request with
        | Error _ -> invalidArg (nameof request) "Invalid case-request format."
        | Ok value -> codec.Write value |> CanonicalWriter.bytes
