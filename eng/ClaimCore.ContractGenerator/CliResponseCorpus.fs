namespace ClaimCore.ContractGeneration

open System
open System.Buffers
open System.Text.Json
open ClaimCore.Contracts

[<NoEquality; NoComparison>]
type private CliV4Case =
    {
        Identifier: string
        Endpoint: string option
        ExitCode: int
        Valid: bool
        Value: JsonElement
    }

[<RequireQualifiedAccess>]
module CliResponseCorpus =
    let private decoded (response: CliWireResponse) =
        use document = JsonDocument.Parse(ReadOnlyMemory response.Bytes)
        document.RootElement.Clone()

    let private webCases projection =
        let _, bytes = WebParsedCorpus.artifact projection
        use document = JsonDocument.Parse(ReadOnlyMemory bytes)

        document.RootElement.GetProperty("cases").EnumerateArray()
        |> Seq.map _.Clone()
        |> Seq.toList

    let private wrap (kind: string) (endpoint: string) (service: JsonElement) =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("protocolVersion", 4)
        writer.WriteString("kind", kind)
        writer.WriteString("endpoint", endpoint)
        writer.WritePropertyName("service")
        service.WriteTo(writer)
        writer.WriteEndObject()
        writer.Flush()
        use document = JsonDocument.Parse(buffer.WrittenMemory)
        document.RootElement.Clone()

    let private sourceEndpoint (identifier: string) =
        if not (identifier.StartsWith("cross-", StringComparison.Ordinal)) then
            None
        else
            identifier.Substring(6).Split("-as-", 2) |> Array.tryHead

    let private fromWeb endpoints (item: JsonElement) =
        let identifier =
            item.GetProperty("id").GetString() |> Option.ofObj |> Option.defaultValue ""

        let target = item.GetProperty("endpoint")
        let valid = item.GetProperty("valid").GetBoolean()
        let service = item.GetProperty("value")

        if
            target.ValueKind = JsonValueKind.Null
            && identifier.EndsWith("-wrong-http-status", StringComparison.Ordinal)
        then
            None
        elif target.ValueKind = JsonValueKind.Null then
            Some
                {
                    Identifier = identifier
                    Endpoint = Some "case.list"
                    ExitCode = 3
                    Valid = valid
                    Value = wrap "serviceFailure" "case.list" service
                }
        else
            let endpoint = target.GetString() |> Option.ofObj |> Option.defaultValue ""

            if
                not (Set.contains endpoint endpoints)
                || (sourceEndpoint identifier
                    |> Option.exists (fun source -> not (Set.contains source endpoints)))
            then
                None
            else
                let response =
                    if valid then
                        CliRemoteWireCodec.result endpoint service
                    else
                        { ExitCode = 2; Bytes = [||] }

                Some
                    {
                        Identifier = identifier
                        Endpoint = Some endpoint
                        ExitCode = response.ExitCode
                        Valid = valid
                        Value =
                            if valid then
                                decoded response
                            else
                                wrap "result" endpoint service
                    }

    let private localCases endpoints =
        endpoints
        |> Set.toList
        |> List.map (fun endpoint ->
            let response =
                CliRemoteWireCodec.localFailure endpoint CliRemoteProblem.Authentication

            {
                Identifier = "local-valid-" + endpoint
                Endpoint = Some endpoint
                ExitCode = response.ExitCode
                Valid = true
                Value = decoded response
            })

    let private serviceFailureCases endpoints =
        let host = WebWireCodec.hostFailure WebHostFailure.SessionRejected
        use document = JsonDocument.Parse(ReadOnlyMemory host)

        endpoints
        |> Set.toList
        |> List.map (fun endpoint ->
            let response = CliRemoteWireCodec.hostFailure endpoint document.RootElement

            {
                Identifier = "valid-service-failure-" + endpoint
                Endpoint = Some endpoint
                ExitCode = response.ExitCode
                Valid = true
                Value = decoded response
            })

    let private specialCases () =
        let exported =
            CliRemoteWireCodec.exported
                "recovery.export"
                (Guid.Parse("10000000-0000-4000-8000-000000000001"))
                "application/vnd.claimcore.recovery+json"

        let protocol =
            ProtocolFailure.create ProtocolProblem.ExpectedObject ProtocolLocation.root
            |> CliRemoteWireCodec.protocolFailure 2

        [
            {
                Identifier = "valid-exported"
                Endpoint = Some "recovery.export"
                ExitCode = exported.ExitCode
                Valid = true
                Value = decoded exported
            }
            {
                Identifier = "valid-protocol-failure"
                Endpoint = None
                ExitCode = protocol.ExitCode
                Valid = true
                Value = decoded protocol
            }
        ]

    let private writeCase (writer: Utf8JsonWriter) item =
        writer.WriteStartObject()
        writer.WriteString("id", item.Identifier)

        match item.Endpoint with
        | Some value -> writer.WriteString("endpoint", value)
        | None -> writer.WriteNull("endpoint")

        writer.WriteNumber("exitCode", item.ExitCode)
        writer.WriteBoolean("valid", item.Valid)
        writer.WritePropertyName("value")
        item.Value.WriteTo(writer)
        writer.WriteEndObject()

    let artifact (projection: ContractModel) =
        let endpoints = projection.CliEndpoints |> List.map _.Identifier |> Set.ofList

        let cases =
            webCases projection
            |> List.choose (fromWeb endpoints)
            |> fun value ->
                value @ localCases endpoints @ serviceFailureCases endpoints @ specialCases ()

        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteStartArray("cases")
        cases |> List.iter (writeCase writer)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()

        "cli-v4.parsed-value-corpus.json",
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
