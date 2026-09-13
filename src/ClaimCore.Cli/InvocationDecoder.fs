namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Domain

[<NoEquality; NoComparison; RequireQualifiedAccess>]
type EndpointInput =
    | Draft of CommandDraft
    | CaseReference of string
    | CaseList of afterReference: string option * limit: int
    | History of caseReference: string * cursor: string option * limit: int * detail: HistoryDetail
    | Operation of Guid
    | RecoveryPage of cursor: string option * limit: int
    | RecoveryResolve of operationId: Guid * requestSha256: string
    | RecoveryDismiss of operationId: Guid * requestSha256: string
    | RecoveryExport of operationId: Guid * requestSha256: string * destination: string
    | RecoveryImportPreview of source: string
    | RecoveryImportRetain of source: string * sourceSha256: string

module InvocationDecoder =
    let private failure code message path =
        Error(ProtocolFailure.create code message path)

    let private requiredOption code message path value =
        match value with
        | Some item -> Ok item
        | None -> failure code message path

    let private canonicalGuid path (value: JsonElement) =
        match StrictJson.stringAt path value with
        | Error problem -> Error problem
        | Ok raw ->
            match Guid.TryParseExact(raw, "D") with
            | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = raw -> Ok parsed
            | _ -> failure "INVALID_UUID" "Use a non-empty canonical lowercase UUID." path

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
        | Ok _ -> failure "INVALID_DIGEST" "Use a lowercase SHA-256 digest." path

    let private revision path (value: JsonElement) =
        match StrictJson.stringAt path value with
        | Error problem -> Error problem
        | Ok raw when raw = "0" -> Ok 0L
        | Ok raw when raw.Length > 0 && raw[0] <> '0' && raw |> Seq.forall Char.IsAsciiDigit ->
            match Int64.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, parsed when parsed < Int64.MaxValue -> Ok parsed
            | _ ->
                failure
                    "INVALID_REVISION"
                    "Use a canonical unsigned revision below Int64.MaxValue."
                    path
        | Ok _ ->
            failure
                "INVALID_REVISION"
                "Use a canonical unsigned revision below Int64.MaxValue."
                path

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

    let private commandKind path (value: JsonElement) =
        StrictJson.stringAt path value
        |> Result.bind (fun token ->
            CommandKinds.all
            |> List.tryFind (fun kind -> CommandKinds.token kind = token)
            |> requiredOption
                "INVALID_COMMAND"
                "The command kind is not declared by the semantic contract."
                path)

    let private values kind (value: JsonElement) =
        let expected = CommandDefinitions.forKind kind |> _.Inputs |> List.map _.FieldName

        StrictJson.exactProperties "/input/command/values" expected value
        |> Result.bind (fun source ->
            expected
            |> List.map (fun name ->
                StrictJson.requiredProperty "/input/command/values" name source
                |> Result.bind (StrictJson.stringAt ("/input/command/values/" + name))
                |> Result.map (fun text -> name, text))
            |> List.fold
                (fun state item ->
                    match state, item with
                    | Ok collected, Ok decoded -> Ok(decoded :: collected)
                    | Error problem, _ -> Error problem
                    | _, Error problem -> Error problem)
                (Ok [])
            |> Result.map List.rev)

    let private draft (input: JsonElement) =
        let command source =
            StrictJson.requiredProperty "/input" "command" source
            |> Result.bind (StrictJson.exactProperties "/input/command" [ "kind"; "values" ])

        inputObject [ "operationId"; "caseReference"; "expectedRevision"; "command" ] input
        |> Result.bind (fun source ->
            StrictJson.requiredProperty "/input" "operationId" source
            |> Result.bind (canonicalGuid "/input/operationId")
            |> Result.bind (fun operationId ->
                requiredString "/input" "caseReference" source
                |> Result.bind (fun caseReference ->
                    StrictJson.requiredProperty "/input" "expectedRevision" source
                    |> Result.bind (revision "/input/expectedRevision")
                    |> Result.bind (fun expectedVersion ->
                        command source
                        |> Result.bind (fun commandValue ->
                            StrictJson.requiredProperty "/input/command" "kind" commandValue
                            |> Result.bind (commandKind "/input/command/kind")
                            |> Result.bind (fun kind ->
                                StrictJson.requiredProperty
                                    "/input/command"
                                    "values"
                                    commandValue
                                |> Result.bind (values kind)
                                |> Result.map (fun fields ->
                                    EndpointInput.Draft
                                        {
                                            OperationId = operationId
                                            CaseReference = caseReference
                                            ExpectedVersion = expectedVersion
                                            Kind = kind
                                            Values = fields
                                        })))))))

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
        StrictJson.allowedProperties "/input" [ "limit" ] [ "cursor"; "limit" ] input
        |> Result.bind (fun source ->
            optionalString "/input" "cursor" source
            |> Result.bind (fun cursor ->
                requiredInteger "/input" "limit" 1 50 source
                |> Result.map (fun limit -> EndpointInput.RecoveryPage(cursor, limit))))

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
                    failure
                        "AFFIRMATION_REQUIRED"
                        "Recovery dismissal requires confirmed: true."
                        "/input/confirmed"))
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
                    failure
                        "AFFIRMATION_REQUIRED"
                        "Recovery retention requires confirmed: true."
                        "/input/confirmed"))
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
                    Endpoint.CommandPrepare, draft
                    Endpoint.CommandExecute, draft
                    Endpoint.CaseGet, caseReference
                    Endpoint.CaseList, listInput
                    Endpoint.CaseHistory, history
                    Endpoint.OperationObserve, operation
                    Endpoint.RecoveryList, recoveryPage
                    Endpoint.RecoveryInspect, operation
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
