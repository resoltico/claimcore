namespace ClaimCore.ContractGeneration

open System
open System.Buffers
open System.Globalization
open System.Text
open System.Text.Json
open ClaimCore.Contracts

[<NoEquality; NoComparison>]
type private RawCase =
    {
        Identifier: string
        Bytes: byte array
        Valid: bool
        ExpectedCode: string option
        ExpectedPath: string option
    }

[<RequireQualifiedAccess>]
module CliRawCorpus =
    let private utf8 value = Encoding.UTF8.GetBytes(value: string)

    let private invocation (endpoint: CliEndpoint) =
        let input = Schema.sampleBytes endpoint.Input
        use inputDocument = JsonDocument.Parse(ReadOnlyMemory<byte>(input))
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("protocolVersion", 3)
        writer.WriteString("endpoint", endpoint.Identifier)
        writer.WritePropertyName("input")
        inputDocument.RootElement.WriteTo(writer)
        writer.WriteEndObject()
        writer.Flush()
        buffer.WrittenSpan.ToArray()

    let private valid (endpoint: CliEndpoint) =
        {
            Identifier = "valid-" + endpoint.Identifier
            Bytes = invocation endpoint
            Valid = true
            ExpectedCode = None
            ExpectedPath = None
        }

    let private invalid identifier code path bytes =
        {
            Identifier = identifier
            Bytes = bytes
            Valid = false
            ExpectedCode = Some code
            ExpectedPath = Some path
        }

    let private close revision operationId =
        utf8 (
            $"{{\"protocolVersion\":3,\"endpoint\":\"command.prepare\",\"input\":{{\"operationId\":\"{operationId}\",\"caseReference\":\"SYNTHETIC-CASE\",\"expectedRevision\":\"{revision}\",\"command\":{{\"kind\":\"CLOSE\",\"values\":{{}}}}}}}}"
        )

    let private digest value =
        utf8 (
            $"{{\"protocolVersion\":3,\"endpoint\":\"recovery.resolve\",\"input\":{{\"operationId\":\"10000000-0000-4000-8000-000000000001\",\"requestSha256\":\"{value}\"}}}}"
        )

    let private correctCase operationId =
        utf8 (
            """{"protocolVersion":3,"endpoint":"command.prepare","input":{"operationId":"""
            + JsonSerializer.Serialize(operationId)
            + """, "caseReference":"SYNTHETIC-CASE","expectedRevision":"3","command":{"kind":"CORRECT_CASE","groups":{"registration":{"mode":"REPLACE","values":{"incidentDate":"2026-08-01","incidentNotificationDate":"2026-08-03","incidentCountry":"Latvia","claimantName":"Synthetic Claimant","insurerName":"Synthetic Insurer","claimedAmount":"1000.00","claimedCurrency":"EUR"}},"decision":{"mode":"REPLACE","values":{"paymentDecisionDate":"2026-08-15","payableAmount":"750.00","payableCurrency":"EUR"}},"payment":{"mode":"REPLACE","values":{"paymentDate":"2026-08-20"}}}}}}"""
        )

    let private structuralCases =
        let ordinary =
            utf8 "{\"protocolVersion\":3,\"endpoint\":\"case.list\",\"input\":{\"limit\":1}}"

        let duplicate =
            utf8
                "{\"protocolVersion\":3,\"protocolVersion\":3,\"endpoint\":\"case.list\",\"input\":{\"limit\":1}}"

        let deep = utf8 (String.replicate 65 "[" + String.replicate 65 "]")
        let tooLarge = Array.create 131073 (byte ' ')

        [
            invalid "invalid-utf8" "INVALID_UTF8" "" [| 0xFFuy |]
            invalid
                "utf8-bom"
                "UTF8_BOM_FORBIDDEN"
                ""
                (Array.concat [ [| 0xEFuy; 0xBBuy; 0xBFuy |]; ordinary ])
            invalid "duplicate-key" "DUPLICATE_KEY" "" duplicate
            invalid
                "escaped-lone-surrogate-property"
                "INVALID_UNICODE"
                ""
                (utf8
                    """{"protocolVersion":3,"endpoint":"case.list","input":{"\uD800":1,"limit":1}}""")
            invalid
                "escaped-lone-surrogate-value"
                "INVALID_UNICODE"
                "/input/caseReference"
                (utf8
                    """{"protocolVersion":3,"endpoint":"case.get","input":{"caseReference":"\uD800"}}""")
            invalid "trailing-document" "INVALID_JSON" "" (Array.concat [ ordinary; utf8 "{}" ])
            invalid "wrong-root-kind" "INVALID_SHAPE" "" (utf8 "[]")
            invalid "excessive-depth" "INVALID_JSON" "" deep
            invalid "oversize" "INPUT_TOO_LARGE" "" tooLarge
        ]

    let private scalarCases =
        let maximumRevision = Int64.MaxValue.ToString(CultureInfo.InvariantCulture)

        let largestAllowedRevision =
            (Int64.MaxValue - 1L).ToString(CultureInfo.InvariantCulture)

        let overflowRevision =
            (uint64 Int64.MaxValue + 1UL).ToString(CultureInfo.InvariantCulture)

        let operationId = "10000000-0000-4000-8000-000000000001"

        [
            {
                Identifier = "valid-largest-allowed-revision"
                Bytes = close largestAllowedRevision operationId
                Valid = true
                ExpectedCode = None
                ExpectedPath = None
            }
            invalid
                "noncanonical-revision"
                "INVALID_REVISION"
                "/input/expectedRevision"
                (close "01" operationId)
            invalid
                "maximum-revision"
                "INVALID_REVISION"
                "/input/expectedRevision"
                (close maximumRevision operationId)
            invalid
                "overflow-revision"
                "INVALID_REVISION"
                "/input/expectedRevision"
                (close overflowRevision operationId)
            invalid
                "noncanonical-uuid"
                "INVALID_UUID"
                "/input/operationId"
                (close "0" "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA")
            invalid
                "noncanonical-digest"
                "INVALID_DIGEST"
                "/input/requestSha256"
                (digest (String.replicate 64 "A"))
        ]

    let private groupedCorrectionCase =
        {
            Identifier = "valid-correct-case-groups"
            Bytes = correctCase "10000000-0000-4000-8000-000000000001"
            Valid = true
            ExpectedCode = None
            ExpectedPath = None
        }

    let private boundaryCases =
        structuralCases @ scalarCases @ [ groupedCorrectionCase ]

    let private renderCase (writer: Utf8JsonWriter) value =
        writer.WriteStartObject()
        writer.WriteString("id", value.Identifier)
        writer.WriteString("bytesBase64", Convert.ToBase64String(value.Bytes))
        writer.WriteBoolean("valid", value.Valid)

        match value.ExpectedCode with
        | Some expected -> writer.WriteString("expectedCode", expected)
        | None -> writer.WriteNull("expectedCode")

        match value.ExpectedPath with
        | Some expected -> writer.WriteString("expectedPath", expected)
        | None -> writer.WriteNull("expectedPath")

        writer.WriteEndObject()

    let artifact (projection: ContractModel) =
        let identifiers = projection.CliEndpoints |> List.map _.Identifier

        if identifiers.Length <> (identifiers |> Set.ofList |> Set.count) then
            invalidOp "CLI raw corpus cannot contain duplicate endpoint identifiers."

        let cases = (projection.CliEndpoints |> List.map valid) @ boundaryCases
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteStartArray("cases")
        cases |> List.iter (renderCase writer)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()

        "cli-v3.raw-decoder-corpus.json",
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
