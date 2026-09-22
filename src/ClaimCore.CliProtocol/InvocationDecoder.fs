namespace ClaimCore.Cli

open ClaimCore.Contracts

open System
open System.Text.Json
open ClaimCore.Application

module InvocationDecoder =
    let private failure reason path =
        Error(ProtocolFailure.create reason (ProtocolLocation.fromPath path))

    let private canonicalGuid path (value: JsonElement) =
        match StrictJson.stringAt path value with
        | Error problem -> Error problem
        | Ok raw ->
            match Guid.TryParseExact(raw, "D") with
            | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = raw -> Ok parsed
            | _ -> failure ProtocolProblem.InvalidUuid path

    let private digest path (value: JsonElement) =
        match StrictJson.stringAt path value with
        | Error problem -> Error problem
        | Ok raw when
            raw.Length = 64
            && (raw
                |> Seq.forall (fun character ->
                    Char.IsAsciiHexDigit(character) && not (Char.IsUpper(character))))
            ->
            Ok raw
        | Ok _ -> failure ProtocolProblem.InvalidDigest path

    let private inputObject expected (value: JsonElement) =
        StrictJson.exactProperties "/input" expected value

    let private optionalString path name input =
        match StrictJson.optionalProperty name input with
        | None -> Ok None
        | Some value -> StrictJson.stringAt (path + "/" + name) value |> Result.map Some

    let private requiredString path name input =
        StrictJson.requiredProperty path name input
        |> Result.bind (StrictJson.stringAt (path + "/" + name))

    let private requiredInteger path name minimum maximum input =
        StrictJson.requiredProperty path name input
        |> Result.bind (StrictJson.integerAt (path + "/" + name) minimum maximum)


    let private caseReference input =
        inputObject [ "caseReference" ] input
        |> Result.bind (requiredString "/input" "caseReference")
        |> Result.map EndpointInput.CaseReference

    let private listInput input =
        StrictJson.allowedProperties "/input" [ "limit" ] [ "cursor"; "limit" ] input
        |> Result.bind (fun source ->
            optionalString "/input" "cursor" source
            |> Result.bind (fun cursor ->
                requiredInteger "/input" "limit" 1 50 source
                |> Result.map (fun limit -> EndpointInput.CaseList(cursor, limit))))

    let private history input =
        StrictJson.allowedProperties
            "/input"
            [ "caseReference"; "limit"; "detail" ]
            [ "caseReference"; "cursor"; "limit"; "detail" ]
            input
        |> Result.bind (fun source ->
            requiredString "/input" "caseReference" source
            |> Result.bind (fun reference ->
                optionalString "/input" "cursor" source
                |> Result.bind (fun cursor ->
                    requiredInteger "/input" "limit" 1 50 source
                    |> Result.bind (fun limit ->
                        requiredString "/input" "detail" source
                        |> Result.bind (StrictJson.oneOf "/input/detail" [ "SUMMARY"; "FULL" ])
                        |> Result.map (fun detail ->
                            let mode =
                                if detail = "SUMMARY" then
                                    HistoryDetail.Summary
                                else
                                    HistoryDetail.Full

                            EndpointInput.History(reference, cursor, limit, mode))))))

    let private operation input =
        inputObject [ "operationId" ] input
        |> Result.bind (StrictJson.requiredProperty "/input" "operationId")
        |> Result.bind (canonicalGuid "/input/operationId")
        |> Result.map EndpointInput.Operation

    let private recoveryPage input =
        StrictJson.allowedProperties "/input" [ "limit" ] [ "cursor"; "limit"; "view" ] input
        |> Result.bind (fun source ->
            optionalString "/input" "cursor" source
            |> Result.bind (fun cursor ->
                requiredInteger "/input" "limit" 1 50 source
                |> Result.bind (fun limit ->
                    match optionalString "/input" "view" source with
                    | Ok None ->
                        Ok(EndpointInput.RecoveryPage(RecoveryListView.Pending, cursor, limit))
                    | Ok(Some "PENDING") ->
                        Ok(EndpointInput.RecoveryPage(RecoveryListView.Pending, cursor, limit))
                    | Ok(Some "TERMINAL") ->
                        Ok(EndpointInput.RecoveryPage(RecoveryListView.Terminal, cursor, limit))
                    | Ok(Some _) -> failure ProtocolProblem.RecoveryView "/input/view"
                    | Error problem -> Error problem)))

    let private recoveryInspect input =
        StrictJson.allowedProperties
            "/input"
            [ "operationId"; "attemptLimit" ]
            [ "operationId"; "attemptCursor"; "attemptLimit" ]
            input
        |> Result.bind (fun source ->
            StrictJson.requiredProperty "/input" "operationId" source
            |> Result.bind (canonicalGuid "/input/operationId")
            |> Result.bind (fun operationId ->
                optionalString "/input" "attemptCursor" source
                |> Result.bind (fun cursor ->
                    requiredInteger "/input" "attemptLimit" 1 50 source
                    |> Result.map (fun limit ->
                        EndpointInput.RecoveryInspect(operationId, cursor, limit)))))

    let private identityFrom source =
        StrictJson.requiredProperty "/input" "operationId" source
        |> Result.bind (canonicalGuid "/input/operationId")
        |> Result.bind (fun operationId ->
            StrictJson.requiredProperty "/input" "requestSha256" source
            |> Result.bind (digest "/input/requestSha256")
            |> Result.map (fun requestSha256 -> operationId, requestSha256))

    let private recoveryIdentity input =
        inputObject [ "operationId"; "requestSha256" ] input |> Result.bind identityFrom

    let private recoveryDismiss input =
        inputObject [ "operationId"; "requestSha256"; "confirmed" ] input
        |> Result.bind (fun source ->
            StrictJson.requiredProperty "/input" "confirmed" source
            |> Result.bind (StrictJson.boolAt "/input/confirmed")
            |> Result.bind (fun confirmed ->
                if confirmed then
                    identityFrom source
                else
                    failure ProtocolProblem.DismissConfirmation "/input/confirmed"))
        |> Result.map EndpointInput.RecoveryDismiss

    let private recoveryExport input =
        inputObject [ "operationId"; "requestSha256"; "destination" ] input
        |> Result.bind (fun source ->
            identityFrom source
            |> Result.bind (fun (operationId, requestSha256) ->
                requiredString "/input" "destination" source
                |> Result.map (fun destination ->
                    EndpointInput.RecoveryExport(operationId, requestSha256, destination))))

    let private importPreview input =
        inputObject [ "source" ] input
        |> Result.bind (requiredString "/input" "source")
        |> Result.map EndpointInput.RecoveryImportPreview

    let private importRetain input =
        inputObject [ "source"; "sourceSha256"; "confirmed" ] input
        |> Result.bind (fun source ->
            StrictJson.requiredProperty "/input" "confirmed" source
            |> Result.bind (StrictJson.boolAt "/input/confirmed")
            |> Result.bind (fun confirmed ->
                if confirmed then
                    Ok source
                else
                    failure ProtocolProblem.RetainConfirmation "/input/confirmed"))
        |> Result.bind (fun source ->
            requiredString "/input" "source" source
            |> Result.bind (fun path ->
                StrictJson.requiredProperty "/input" "sourceSha256" source
                |> Result.bind (digest "/input/sourceSha256")
                |> Result.map (fun sourceSha256 ->
                    EndpointInput.RecoveryImportRetain(path, sourceSha256))))

    let decodeEndpoint endpoint =
        let handlers =
            Map.ofList
                [
                    Endpoint.CommandPrepare, CliCommandInput.draft
                    Endpoint.CommandExecute, CliCommandInput.draft
                    Endpoint.CaseGet, caseReference
                    Endpoint.CaseList, listInput
                    Endpoint.CaseHistory, history
                    Endpoint.OperationObserve, operation
                    Endpoint.RecoveryList, recoveryPage
                    Endpoint.RecoveryInspect, recoveryInspect
                    Endpoint.RecoveryResolve,
                    (fun input ->
                        recoveryIdentity input |> Result.map EndpointInput.RecoveryResolve)
                    Endpoint.RecoveryDismiss, recoveryDismiss
                    Endpoint.RecoveryExport, recoveryExport
                    Endpoint.RecoveryImportEnvelopePreview, importPreview
                    Endpoint.RecoveryImportEnvelopeRetain, importRetain
                    Endpoint.RecoveryImportRecordPreview, importPreview
                    Endpoint.RecoveryImportRecordRetain, importRetain
                ]

        Map.find endpoint handlers
