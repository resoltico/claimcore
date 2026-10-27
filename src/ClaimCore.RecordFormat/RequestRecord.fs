namespace ClaimCore.RecordFormat

open ClaimCore.Json
open ClaimCore.Domain

module RequestRecord =
    let private codec = RecordCodecs.caseOperation |> Codec.named "CaseRequestRecord"

    let private validate request =
        match Case.parse request with
        | Ok command -> Ok command
        | Error(Refusal.InvalidInput(target, _)) ->
            Error
                {
                    Field = "$." + DurableNames.inputTarget target
                    Message = "Invalid case-request format."
                }
        | Error _ ->
            Error
                {
                    Field = "$.act"
                    Message = "Invalid case-request format."
                }

    let decode maxBytes bytes =
        Codec.decode (min maxBytes RecordLimits.MaximumRequestRecordBytes) codec bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })
        |> Result.bind validate

    let encode (command: CaseCommand) =
        codec.Write command.Authored
        |> CanonicalWriter.bytes
        |> RecordLimits.requestBytes
