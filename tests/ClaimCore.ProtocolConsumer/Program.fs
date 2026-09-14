module ClaimCore.ProtocolConsumer.Program

open System.Text
open ClaimCore.Protocol

[<EntryPoint>]
let main _ =
    let request: CaseGetRequest = { CaseReference = "SYNTHETIC-1" }

    match WebV2.caseGet.Request.Encode(4096, request) with
    | Error _ -> 1
    | Ok bytes ->
        if Encoding.UTF8.GetString(bytes) <> "{\"caseReference\":\"SYNTHETIC-1\"}" then
            2
        else
            let reply =
                Encoding.UTF8.GetBytes(
                    "{\"endpoint\":\"case.get\",\"outcome\":{\"tag\":\"SUCCEEDED\",\"data\":{\"tag\":\"NOT_FOUND\",\"caseReference\":\"SYNTHETIC-1\"}}}"
                )

            match WebV2.caseGet.Response.Decode(4096, reply) with
            | Ok result ->
                match result.Outcome with
                | CaseGetResponseOutcome.Succeeded succeeded ->
                    match succeeded.Data with
                    | CaseGetResponseOutcomeSucceededData.NotFound missing when
                        missing.CaseReference = request.CaseReference
                        ->
                        0
                    | _ -> 3
                | _ -> 4
            | Error _ -> 5
