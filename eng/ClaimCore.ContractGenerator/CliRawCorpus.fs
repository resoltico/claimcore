namespace ClaimCore.ContractGeneration

open System
open System.Buffers
open System.Text
open System.Text.Json
open ClaimCore.Contracts

[<NoEquality; NoComparison>]
type private CliRawCase =
    {
        Identifier: string
        Bytes: byte array
        Valid: bool
        Code: string option
        Path: string option
    }

[<RequireQualifiedAccess>]
module CliRawCorpus =
    let private utf8 (value: string) = Encoding.UTF8.GetBytes(value)

    let private frame (endpoint: string) (input: byte array) =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("protocolVersion", 4)
        writer.WriteString("endpoint", endpoint)
        writer.WritePropertyName("input")
        use document = JsonDocument.Parse(ReadOnlyMemory input)
        document.RootElement.WriteTo writer
        writer.WriteEndObject()
        writer.Flush()
        buffer.WrittenSpan.ToArray()

    let private valid (endpoint: CliEndpoint) =
        {
            Identifier = "valid-" + endpoint.Identifier
            Bytes = endpoint.Input |> SchemaSamples.bytes |> frame endpoint.Identifier
            Valid = true
            Code = None
            Path = None
        }

    let private invalid identifier code path source =
        {
            Identifier = identifier
            Bytes = utf8 source
            Valid = false
            Code = Some code
            Path = Some path
        }

    let private negative =
        let revision (value: string) =
            $"{{\"protocolVersion\":4,\"endpoint\":\"command.prepare\",\"input\":{{\"operationId\":\"10000000-0000-4000-8000-000000000001\",\"caseReference\":\"SYNTHETIC-CASE\",\"expectedRevision\":\"{value}\",\"command\":{{\"kind\":\"CLOSE\",\"values\":{{}}}}}}}}"

        [
            {
                Identifier = "valid-largest-allowed-revision"
                Bytes = revision ((Int64.MaxValue - 1L).ToString()) |> utf8
                Valid = true
                Code = None
                Path = None
            }
            invalid
                "maximum-revision"
                "INVALID_VALUE"
                "/input/expectedRevision"
                (revision (Int64.MaxValue.ToString()))
            invalid
                "overflow-revision"
                "INVALID_VALUE"
                "/input/expectedRevision"
                (revision "9223372036854775808")
            invalid
                "old-v3-refused"
                "INVALID_RANGE"
                "/protocolVersion"
                """{"protocolVersion":3,"endpoint":"case.list","input":{"limit":1}}"""
            invalid
                "unknown-endpoint"
                "UNKNOWN_ENDPOINT"
                "/endpoint"
                """{"protocolVersion":4,"endpoint":"case.unknown","input":{}}"""
            invalid
                "invalid-input"
                "INVALID_RANGE"
                "/input/limit"
                """{"protocolVersion":4,"endpoint":"case.list","input":{"limit":0}}"""
            invalid
                "duplicate-key"
                "DUPLICATE_KEY"
                ""
                """{"protocolVersion":4,"protocolVersion":4,"endpoint":"case.list","input":{"limit":1}}"""
            {
                Identifier = "invalid-utf8"
                Bytes = [| 0xFFuy |]
                Valid = false
                Code = Some "INVALID_UTF8"
                Path = Some ""
            }
        ]

    let private writeCase (writer: Utf8JsonWriter) item =
        writer.WriteStartObject()
        writer.WriteString("id", item.Identifier)
        writer.WriteString("bytesBase64", Convert.ToBase64String(item.Bytes))
        writer.WriteBoolean("valid", item.Valid)

        match item.Code, item.Path with
        | Some code, Some path ->
            writer.WriteString("expectedCode", code)
            writer.WriteString("expectedPath", path)
        | _ ->
            writer.WriteNull("expectedCode")
            writer.WriteNull("expectedPath")

        writer.WriteEndObject()

    let artifact (projection: ContractModel) =
        let cases = (projection.CliEndpoints |> List.map valid) @ negative
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteStartArray("cases")
        cases |> List.iter (writeCase writer)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()

        "cli-v4.raw-decoder-corpus.json",
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
