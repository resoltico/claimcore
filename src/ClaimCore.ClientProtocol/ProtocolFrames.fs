namespace ClaimCore.ClientProtocol

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ProtocolJson

type ClientEndpoint =
    {
        Identifier: string
        Method: string
        Path: string
        Mutates: bool
        AcceptingScope: string option
        ReceiptScope: string option
        Request: SchemaDocument
        Response: SchemaDocument
    }

type ProtocolCatalogue =
    {
        Endpoints: ClientEndpoint list
        Invocation: SchemaDocument
        FrameResponse: SchemaDocument
    }

[<NoComparison>]
type ProtocolInvocation =
    private
        {
            Endpoint: ClientEndpoint
            Input: JsonElement
            Bytes: byte array
            InputBytes: byte array
        }

module ProtocolFrames =
    let endpoint (frame: ProtocolInvocation) = frame.Endpoint
    let input (frame: ProtocolInvocation) = frame.Input
    let bytes (frame: ProtocolInvocation) = Array.copy frame.Bytes
    let inputBytes (frame: ProtocolInvocation) = Array.copy frame.InputBytes

    let parse catalogue bytes =
        HttpInputSupport.parse bytes (fun root ->
            if not (SchemaValueValidation.verify catalogue.Invocation root) then
                HttpInputSupport.fail HttpInputProblem.InvalidJson

            let id = root.GetProperty("endpoint").GetString() |> nonNull
            let input = root.GetProperty("input")

            {
                Endpoint =
                    catalogue.Endpoints |> List.find (fun endpoint -> endpoint.Identifier = id)
                Input = input.Clone()
                Bytes = Array.copy bytes
                InputBytes = Encoding.UTF8.GetBytes(input.GetRawText())
            })

    let private string (name: string) (value: JsonElement) =
        value.GetProperty(name).GetString() |> nonNull

    let correlate (frame: ProtocolInvocation) (reply: JsonElement) =
        CorrelationPolicy.evaluate (defaultArg frame.Endpoint.ReceiptScope "NONE") frame.Input reply

    let decorate endpoint operation knowledge evidence (value: JsonNode) =
        value["endpoint"] <- optional text endpoint
        value["operationId"] <- optional text operation
        value["attemptKnowledge"] <- text knowledge
        value["resolutionEvidence"] <- text evidence
        value.ToJsonString() |> Encoding.UTF8.GetBytes

    let private operation (frame: ProtocolInvocation) =
        match frame.Input.TryGetProperty "operationId" with
        | true, value -> value.GetString() |> Option.ofObj
        | _ -> None

    let result (frame: ProtocolInvocation) response =
        HttpInputSupport.parse response (fun root ->
            if
                not (SchemaValueValidation.verify frame.Endpoint.Response root)
                || not (correlate frame root)
            then
                HttpInputSupport.fail HttpInputProblem.InvalidJson

            let outcome = root.GetProperty "outcome"
            let knowledge = DeliveryPolicy.outcome frame.Endpoint.Mutates (string "tag" outcome)

            let diagnostic =
                if string "tag" outcome = "REJECTED" then
                    string "id" ((outcome.GetProperty "data").GetProperty "diagnostic")
                else
                    ""

            let evidence =
                DeliveryPolicy.resolutionEvidence
                    frame.Endpoint.ReceiptScope.IsSome
                    knowledge
                    (string "tag" outcome)
                    diagnostic

            obj [ "kind", text "result"; "outcome", JsonNode.Parse(outcome.GetRawText()) ]
            |> decorate (Some frame.Endpoint.Identifier) (operation frame) knowledge evidence)

    let serviceFailure (frame: ProtocolInvocation) reason =
        obj
            [
                "kind", text "serviceFailure"
                "failure", JsonNode.Parse(HostFailures.encode reason)
            ]
        |> decorate
            (Some frame.Endpoint.Identifier)
            (operation frame)
            (DeliveryPolicy.host frame.Endpoint.Mutates (HostFailures.phase reason))
            "NONE"

    let failure kind code =
        obj [ "kind", text kind; "code", text code ]
        |> decorate None None "NOT_SENT" "NONE"

    let withKnowledge (frame: ProtocolInvocation) knowledge (bytes: byte array) =
        let value = JsonNode.Parse(ReadOnlySpan<byte> bytes) |> nonNull

        let evidence =
            value["resolutionEvidence"] |> nonNull |> fun item -> item.GetValue<string>()

        decorate (Some frame.Endpoint.Identifier) (operation frame) knowledge evidence value

    let decode catalogue bytes =
        HttpInputSupport.parse bytes (fun root ->
            if not (SchemaValueValidation.verify catalogue.FrameResponse root) then
                HttpInputSupport.fail HttpInputProblem.InvalidJson

            root.Clone())

    let private resultExit (frame: JsonElement) =
        match string "tag" (frame.GetProperty "outcome") with
        | "REJECTED" -> 2
        | "FAILED" -> 3
        | "CANCELLED" -> 130
        | _ -> 0

    let private localExit (frame: JsonElement) =
        match string "code" frame with
        | "CLI_CANCELLED" -> 130
        | "CLI_INTERNAL" -> 70
        | _ -> 3

    let exitCode (frame: JsonElement) =
        if string "attemptKnowledge" frame = "UNCONFIRMED" then
            4
        else
            match string "kind" frame with
            | "protocolFailure" -> 2
            | "localFailure" -> localExit frame
            | "serviceFailure" -> 3
            | "result" -> resultExit frame
            | _ -> invalidArg "frame" "Unknown frame kind."
