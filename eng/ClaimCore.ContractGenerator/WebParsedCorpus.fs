namespace ClaimCore.ContractGeneration

open System
open System.Buffers
open System.Text
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts

[<NoEquality; NoComparison>]
type private WebParsedCase =
    {
        Identifier: string
        Endpoint: string option
        Status: int
        Valid: bool
        Value: string
    }

[<RequireQualifiedAccess>]
module WebParsedCorpus =
    let private parsed (bytes: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        document.RootElement.Clone()

    let private valid
        (identifier: string)
        (endpoint: string option)
        (status: int)
        (bytes: byte array)
        : WebParsedCase =
        {
            Identifier = "valid-" + identifier
            Endpoint = endpoint
            Status = status
            Valid = true
            Value = Encoding.UTF8.GetString(bytes)
        }

    let private invalid
        (identifier: string)
        (endpoint: string option)
        (status: int)
        (value: JsonElement)
        : WebParsedCase =
        {
            Identifier = identifier
            Endpoint = endpoint
            Status = status
            Valid = false
            Value = value.GetRawText()
        }

    let private productionSamples =
        WebQueryCorpusSamples.all
        @ WebRecoveryQueryCorpusSamples.all
        @ WebMutationCorpusSamples.all
        @ WebImportRetainCorpusSamples.all

    let private representatives endpoints =
        endpoints
        |> List.map (fun endpoint ->
            let sample =
                productionSamples
                |> List.tryFind (fun value -> value.Endpoint = endpoint)
                |> Option.defaultWith (fun () ->
                    invalidOp ("Web response corpus lacks endpoint " + endpoint + "."))

            endpoint, sample.Bytes)

    let private malformed (endpoint: string, bytes: byte array) =
        let value = parsed bytes
        let omitOutcome = CorpusJson.rewrite value "outcome" None false
        let extra = CorpusJson.rewrite value "" None true

        let wrongType =
            CorpusJson.rewrite
                value
                "outcome"
                (Some(fun writer -> writer.WriteNumberValue(42)))
                false

        let wrongTag =
            CorpusJson.rewrite
                value
                "outcome"
                (Some(fun writer ->
                    writer.WriteStartObject()
                    writer.WriteString("tag", "NOT_A_TAG")
                    writer.WriteNull("data")
                    writer.WriteEndObject()))
                false

        [
            invalid ("missing-outcome-" + endpoint) (Some endpoint) 200 omitOutcome
            invalid ("extra-property-" + endpoint) (Some endpoint) 200 extra
            invalid ("wrong-outcome-type-" + endpoint) (Some endpoint) 200 wrongType
            invalid ("wrong-outcome-tag-" + endpoint) (Some endpoint) 200 wrongTag
        ]

    let private patternBoundaries samples =
        let representative endpoint =
            samples
            |> List.find (fun (identifier, _) -> identifier = endpoint)
            |> snd
            |> parsed

        [
            "revision", "case.get", "1"
            "amount", "case.get", CliCorpusValues.fields.ClaimedAmount
            "currency", "case.get", CliCorpusValues.fields.ClaimedCurrency
            "digest", "command.prepare", CliCorpusValues.digest
        ]
        |> List.collect (fun (identifier, endpoint, expected) ->
            CorpusJson.prefixedAndSuffixed identifier (representative endpoint) expected
            |> List.map (fun (suffix, value) ->
                invalid ("pattern-" + suffix + "-" + endpoint) (Some endpoint) 200 value))

    let private scalarBoundaries samples =
        let representative endpoint =
            samples
            |> List.find (fun (identifier, _) -> identifier = endpoint)
            |> snd
            |> parsed

        let alpha = WebCorpusSamples.alphaOperationId

        let uuidSource =
            WebWireCodec.prepare (PrepareOutcome.CancelledBeforeAdmission alpha) |> parsed

        ScalarCorpus.all
            (representative "case.get")
            (representative "command.prepare")
            uuidSource
            alpha
            SemanticContract.current.CanonicalCommandFormat
        |> List.map (fun item -> invalid item.Identifier (Some item.Endpoint) 200 item.Value)

    let private crossEndpoint samples =
        [
            for target, _ in samples do
                for source, bytes in samples do
                    if target <> source then
                        yield
                            invalid
                                ("cross-" + source + "-as-" + target)
                                (Some target)
                                200
                                (parsed bytes)
        ]

    let private writeCase (writer: Utf8JsonWriter) (value: WebParsedCase) =
        writer.WriteStartObject()
        writer.WriteString("id", value.Identifier)

        match value.Endpoint with
        | Some endpoint -> writer.WriteString("endpoint", endpoint)
        | None -> writer.WriteNull("endpoint")

        writer.WriteNumber("status", value.Status)
        writer.WriteBoolean("valid", value.Valid)
        writer.WritePropertyName("value")
        writer.WriteRawValue(value.Value, true)
        writer.WriteEndObject()

    let artifact (projection: ContractModel) =
        let endpoints = projection.WebEndpoints |> List.map _.Identifier
        let samples = representatives endpoints
        let sampled = samples |> List.map fst

        if endpoints <> sampled then
            invalidOp "Web parsed corpus must follow the exact generated endpoint inventory."

        WebHostCorpusSamples.assertComplete ()

        let hostFailures =
            WebHostCorpusSamples.all
            |> List.map (fun sample ->
                valid ("host-" + sample.Identifier) None sample.Status sample.Bytes)

        let invalidHost =
            let hostFailure = WebHostCorpusSamples.all |> List.head

            invalid
                "host-failure-missing-code"
                None
                hostFailure.Status
                (CorpusJson.rewrite (parsed hostFailure.Bytes) "code" None false)

        let validResponses =
            productionSamples
            |> List.map (fun sample ->
                valid sample.Identifier (Some sample.Endpoint) 200 sample.Bytes)

        let cases =
            hostFailures
            @ [ invalidHost ]
            @ validResponses
            @ (samples |> List.collect malformed)
            @ patternBoundaries samples
            @ scalarBoundaries samples
            @ crossEndpoint samples

        let identifiers = cases |> List.map _.Identifier

        if identifiers.Length <> (identifiers |> Set.ofList |> Set.count) then
            invalidOp "Web parsed corpus identifiers must be unique."

        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteStartArray("cases")
        cases |> List.iter (writeCase writer)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()

        "web-v2.parsed-value-corpus.json",
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
