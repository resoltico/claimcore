namespace ClaimCore.Contracts

open System
open System.Buffers
open System.Text.Json

[<RequireQualifiedAccess>]
type CliRemoteProblem =
    | Configuration
    | Authentication
    | ServiceUnavailable
    | ServiceReplyInvalid
    | PrivateSource
    | PrivateDestination
    | DeliveryUnconfirmed

module CliRemoteWireCodec =
    let private encode exit write =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        write writer
        writer.Flush()

        {
            ExitCode = exit
            Bytes = Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
        }

    let private problemCode =
        function
        | CliRemoteProblem.Configuration -> "CLI_CONFIGURATION_INVALID"
        | CliRemoteProblem.Authentication -> "CLI_AUTHENTICATION_UNAVAILABLE"
        | CliRemoteProblem.ServiceUnavailable -> "CLI_SERVICE_UNAVAILABLE"
        | CliRemoteProblem.ServiceReplyInvalid -> "CLI_SERVICE_REPLY_INVALID"
        | CliRemoteProblem.PrivateSource -> "CLI_PRIVATE_SOURCE_INVALID"
        | CliRemoteProblem.PrivateDestination -> "CLI_PRIVATE_DESTINATION_INVALID"
        | CliRemoteProblem.DeliveryUnconfirmed -> "CLI_DELIVERY_UNCONFIRMED"

    let localFailure (endpoint: string) problem =
        let uncertain = problem = CliRemoteProblem.DeliveryUnconfirmed

        encode (if uncertain then 4 else 3) (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("protocolVersion", 4)
            writer.WriteString("kind", "localFailure")
            writer.WriteString("endpoint", endpoint)
            writer.WriteString("code", problemCode problem)

            writer.WriteString(
                "executionPhase",
                if uncertain then
                    "STARTED_UNCONFIRMED"
                elif problem = CliRemoteProblem.PrivateDestination then
                    "RESULT_OBSERVED"
                else
                    "NOT_STARTED"
            )

            writer.WriteString(
                "action",
                if uncertain then
                    "RECOVER_EXACT"
                else
                    "STOP_AND_INVESTIGATE"
            )

            writer.WriteEndObject())

    let protocolFailure exitCode (failure: ProtocolFailure) =
        encode exitCode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("protocolVersion", 4)
            writer.WriteString("kind", "protocolFailure")
            writer.WriteString("code", failure.Code)
            writer.WriteString("diagnosticId", ProtocolProblems.token failure.Reason)
            writer.WriteString("path", failure.Path)
            writer.WriteEndObject())

    /// Core recovery direction preserves earlier uncertainty even before a new attempt.
    let private recoverExact (fault: JsonElement) =
        let mutable action = Unchecked.defaultof<JsonElement>

        fault.ValueKind = JsonValueKind.Object
        && fault.TryGetProperty("recommendedAction", &action)
        && action.ValueKind = JsonValueKind.String
        && action.GetString() = "RECOVER_EXACT"

    let private faultDeliveryUnconfirmed (data: JsonElement) =
        let mutable fault = Unchecked.defaultof<JsonElement>

        recoverExact data
        || (data.ValueKind = JsonValueKind.Object
            && data.TryGetProperty("fault", &fault)
            && recoverExact fault)

    let private executionExit (outcome: JsonElement) =
        let data = outcome.GetProperty("data")
        let settlement = data.GetProperty("settlement").GetString()

        if settlement = "UNCONFIRMED" then
            4
        else
            let execution = data.GetProperty("execution")

            match execution.GetProperty("tag").GetString() with
            | "ACCEPTED" -> 0
            | "REJECTED"
            | "REVOKED_BEFORE_EXECUTION" -> 2
            | _ -> if faultDeliveryUnconfirmed execution then 4 else 3

    let private uncertainOutcomes =
        Set.ofList
            [
                "PREPARATION_STATE_UNKNOWN"
                "ATTEMPT_ADMISSION_UNKNOWN"
                "ATTEMPT_UNRESOLVED"
                "RETAIN_STATE_UNKNOWN"
                "DISMISS_STATE_UNKNOWN"
                "STARTED_UNCONFIRMED"
                "UNCONFIRMED"
            ]

    let private cancelledOutcomes =
        Set.ofList [ "CANCELLED"; "CANCELLED_BEFORE_ADMISSION"; "CANCELLED_BEFORE_ATTEMPT" ]

    let private refusedOutcomes =
        Set.ofList
            [
                "REJECTED"
                "REFUSED"
                "NOT_FOUND"
                "RETAINED_FOR_RECOVERY"
                "REFUSED_BEFORE_ATTEMPT"
            ]

    let private successfulOutcomes =
        Set.ofList
            [
                "PREPARED"
                "OBSERVED_ACCEPTED"
                "DISMISSED"
                "ALREADY_DISMISSED"
                "ALREADY_REVOKED"
                "RETAINED"
                "EXISTING"
                "APPLIED"
                "APPROVED"
                "AVAILABLE"
                "DESCRIBED"
                "SNAPSHOT"
                "REVOKED"
            ]


    let private lookupExit (data: JsonElement) =
        let mutable inner = Unchecked.defaultof<JsonElement>

        if
            data.ValueKind = JsonValueKind.Object
            && data.TryGetProperty("tag", &inner)
            && inner.ValueKind = JsonValueKind.String
            && inner.GetString() = "NOT_FOUND"
        then
            2
        else
            0


    let private outcomeExit (response: JsonElement) =
        let outcome = response.GetProperty("outcome")
        let tag = outcome.GetProperty("tag").GetString()

        if
            (tag = "FAILED" || tag = "FAILED_BEFORE_ATTEMPT")
            && faultDeliveryUnconfirmed (outcome.GetProperty("data"))
        then
            4
        elif tag = "COMPLETED" then
            executionExit outcome
        elif tag = "SUCCEEDED" then
            lookupExit (outcome.GetProperty("data"))
        elif Set.contains tag uncertainOutcomes then
            4
        elif Set.contains tag cancelledOutcomes then
            130
        elif Set.contains tag refusedOutcomes then
            2
        elif Set.contains tag successfulOutcomes then
            0
        else
            3

    let result (endpoint: string) (response: JsonElement) =
        let exitCode = outcomeExit response

        encode exitCode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("protocolVersion", 4)
            writer.WriteString("kind", "result")
            writer.WriteString("endpoint", endpoint)
            writer.WritePropertyName("service")
            response.WriteTo(writer)
            writer.WriteEndObject())

    let hostFailure (endpoint: string) (response: JsonElement) =
        let phase = response.GetProperty("executionPhase")

        let mutating = CliMutationCatalog.isMutation endpoint

        let diagnostic = response.GetProperty("diagnostic").GetProperty("id").GetString()

        let uncertain =
            (phase.ValueKind = JsonValueKind.String
             && phase.GetString() = "STARTED_UNCONFIRMED")
            || (mutating && diagnostic = "WEB_HOST_COMPLETED_RESPONSE_FAILED")

        encode (if uncertain then 4 else 3) (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("protocolVersion", 4)
            writer.WriteString("kind", "serviceFailure")
            writer.WriteString("endpoint", endpoint)
            writer.WritePropertyName("service")
            response.WriteTo(writer)
            writer.WriteEndObject())

    let exported (endpoint: string) (operationId: Guid) (mediaType: string) =
        encode 0 (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("protocolVersion", 4)
            writer.WriteString("kind", "exported")
            writer.WriteString("endpoint", endpoint)
            writer.WriteString("operationId", operationId)
            writer.WriteString("mediaType", mediaType)
            writer.WriteEndObject())
