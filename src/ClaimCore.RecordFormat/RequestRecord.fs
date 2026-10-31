namespace ClaimCore.RecordFormat

open ClaimCore.Json
open ClaimCore.Domain

module RequestRecord =
    let private codec = RecordCodecs.caseOperation |> Codec.named "CaseRequestRecord"

    let private validate request =
        match Case.parse request with
        | Ok command -> Ok command
        | Error(Refusal.InvalidInput issues) ->
            let target = (InputIssues.head issues).Target

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

    /// Existing-case identity is the resolved retained ID, independent of lookup spelling.
    let identity caseId (command: CaseCommand) =
        let canonical = Case.canonical command

        match codec.Write canonical.Authored with
        | JsonValue.Object members ->
            let members =
                if CaseAct.kind command.Act = CaseActKind.Open then
                    members
                else
                    members |> List.filter (fst >> (<>) DurableNames.CaseReference)

            JsonValue.Object(
                (DurableNames.CaseId, JsonValue.String(CaseId.format caseId)) :: members
            )
            |> CanonicalWriter.bytes
        | _ -> invalidOp "A permanent case request is an object."
