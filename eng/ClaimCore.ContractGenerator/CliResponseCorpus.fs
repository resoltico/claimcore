namespace ClaimCore.ContractGeneration

open System
open System.Buffers
open System.Text
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts

[<NoEquality; NoComparison>]
type private CliParsedCase =
    {
        Identifier: string
        Endpoint: string option
        ExitCode: int
        Valid: bool
        Value: string
    }

[<RequireQualifiedAccess>]
module CliResponseCorpus =
    let private parsed (response: CliWireResponse) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(response.Bytes))
        document.RootElement.Clone()

    let private valid (sample: CliEncodedSample) =
        {
            Identifier = "valid-" + sample.Identifier
            Endpoint = sample.Endpoint
            ExitCode = sample.Response.ExitCode
            Valid = true
            Value = Encoding.UTF8.GetString(sample.Response.Bytes)
        }

    let private invalid identifier endpoint (value: JsonElement) =
        {
            Identifier = identifier
            Endpoint = Some endpoint
            ExitCode = 2
            Valid = false
            Value = value.GetRawText()
        }

    let private json text =
        use document = JsonDocument.Parse(text: string)
        document.RootElement.Clone()

    let private withExtra (value: JsonElement) =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()

        value.EnumerateObject()
        |> Seq.iter (fun property ->
            writer.WritePropertyName(property.Name)
            property.Value.WriteTo(writer))

        writer.WriteBoolean("extra", true)
        writer.WriteEndObject()
        writer.Flush()
        use document = JsonDocument.Parse(buffer.WrittenMemory)
        document.RootElement.Clone()

    let private malformed (endpoint, representative) =
        let prefix = "{\"protocolVersion\":3,\"kind\":\"result\",\"endpoint\":\"" + endpoint

        [
            invalid ("missing-outcome-" + endpoint) endpoint (json (prefix + "\"}"))
            invalid ("extra-property-" + endpoint) endpoint (withExtra representative)
            invalid
                ("wrong-outcome-type-" + endpoint)
                endpoint
                (json (prefix + "\",\"outcome\":42}"))
            invalid
                ("wrong-outcome-tag-" + endpoint)
                endpoint
                (json (prefix + "\",\"outcome\":{\"kind\":\"NOT_A_TAG\"}}"))
        ]

    let private representatives (endpoints: string list) (samples: CliEncodedSample list) =
        endpoints
        |> List.map (fun endpoint ->
            let sample =
                samples
                |> List.tryFind (fun value -> value.Endpoint = Some endpoint)
                |> Option.defaultWith (fun () ->
                    invalidOp ("CLI response corpus lacks endpoint " + endpoint + "."))

            endpoint, parsed sample.Response)

    let private patternBoundaries representatives =
        let representative endpoint =
            representatives
            |> List.find (fun (identifier, _) -> identifier = endpoint)
            |> snd

        [
            "revision", "case.get", "1"
            "amount", "case.get", CliCorpusValues.fields.ClaimedAmount
            "currency", "case.get", CliCorpusValues.fields.ClaimedCurrency
            "digest", "command.prepare", CliCorpusValues.digest
        ]
        |> List.collect (fun (identifier, endpoint, expected) ->
            CorpusJson.prefixedAndSuffixed identifier (representative endpoint) expected
            |> List.map (fun (suffix, value) ->
                invalid ("pattern-" + suffix + "-" + endpoint) endpoint value))

    let private scalarBoundaries representatives =
        let representative endpoint =
            representatives
            |> List.find (fun (identifier, _) -> identifier = endpoint)
            |> snd

        let alpha = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")

        let uuidSource =
            CliWireCodec.prepare "command.prepare" (PrepareOutcome.CancelledBeforeAdmission alpha)
            |> parsed

        ScalarCorpus.all
            (representative "case.get")
            (representative "command.prepare")
            uuidSource
            alpha
            SemanticContract.current.CanonicalCommandFormat
        |> List.map (fun item -> invalid item.Identifier item.Endpoint item.Value)

    let private crossEndpoint (representatives: (string * JsonElement) list) =
        [
            for target, _ in representatives do
                for source, value in representatives do
                    if target <> source then
                        yield invalid ("cross-" + source + "-as-" + target) target value
        ]

    let private writeCase (writer: Utf8JsonWriter) (value: CliParsedCase) =
        writer.WriteStartObject()
        writer.WriteString("id", value.Identifier)

        match value.Endpoint with
        | Some endpoint -> writer.WriteString("endpoint", endpoint)
        | None -> writer.WriteNull("endpoint")

        writer.WriteNumber("exitCode", value.ExitCode)
        writer.WriteBoolean("valid", value.Valid)
        writer.WritePropertyName("value")
        writer.WriteRawValue(value.Value, true)
        writer.WriteEndObject()

    let private productionSamples: CliEncodedSample list =
        CliQueryCorpusSamples.all
        @ CliPrepareCorpusSamples.all
        @ CliMutationCorpusSamples.all
        @ CliRecoveryCorpusSamples.all

    let artifact (projection: ContractModel) =
        let endpoints = projection.CliEndpoints |> List.map _.Identifier
        let representatives = representatives endpoints productionSamples

        let protocolFailure =
            CliWireCodec.protocolFailure 2 "INVALID_SHAPE" "Synthetic safe failure." "/input"

        let protocolCase =
            {
                Identifier = "valid-protocol-failure"
                Endpoint = None
                ExitCode = protocolFailure.ExitCode
                Valid = true
                Value = Encoding.UTF8.GetString(protocolFailure.Bytes)
            }

        let cases =
            protocolCase
            :: ((productionSamples |> List.map valid)
                @ (representatives |> List.collect malformed)
                @ patternBoundaries representatives
                @ scalarBoundaries representatives
                @ crossEndpoint representatives)

        let identifiers = cases |> List.map _.Identifier

        if identifiers.Length <> (identifiers |> Set.ofList |> Set.count) then
            invalidOp "CLI parsed response corpus identifiers must be unique."

        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteStartArray("cases")
        cases |> List.iter (writeCase writer)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()

        "cli-v3.parsed-value-corpus.json",
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
