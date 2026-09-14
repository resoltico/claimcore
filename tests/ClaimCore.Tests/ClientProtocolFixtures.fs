module ClaimCore.Tests.ClientProtocolFixtures

open System
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open Microsoft.FSharp.Reflection
open Expecto
open ClaimCore.Protocol
open ClaimCore.TestSupport

let utf8 (text: string) = Encoding.UTF8.GetBytes(text)
let maximum = 131072

let decoded (codec: JsonCodec<'T>) text =
    match codec.Decode(maximum, utf8 text) with
    | Ok value -> value
    | Error problem -> failtestf "Protocol fixture failed: %s" problem.Code

let encoded (codec: JsonCodec<'T>) value =
    match codec.Encode(maximum, value) with
    | Ok bytes -> bytes
    | Error problem -> failtestf "Protocol value failed: %s" problem.Code

let refused (codec: JsonCodec<'T>) text =
    Expect.isError (codec.Decode(maximum, utf8 text)) "Invalid wire input must be refused"

let decision =
    """{"operationId":"10000000-0000-4000-8000-000000000001","caseReference":"SYNTHETIC-1","expectedRevision":"9007199254740993","command":{"kind":"DECIDE","values":{"paymentDecisionDate":"2024-02-29","payableAmount":"0.00","payableCurrency":"EUR"}}}"""

let fields =
    """{"incidentDate":"2026-01-01","incidentNotificationDate":"2026-01-02","incidentCountry":"Latvia","claimantName":"Synthetic claimant","insurerName":"Synthetic insurer","claimedAmount":"0","claimedCurrency":"EUR","caseReference":"SYNTHETIC-1","paymentDecisionDate":null,"payableAmount":null,"payableCurrency":null,"paymentDate":null,"status":"OPENED"}"""

let current =
    """{"endpoint":"case.get","outcome":{"tag":"SUCCEEDED","data":{"tag":"FOUND","current":{"case":{"fields":FIELDS,"revision":"9007199254740993"},"availableCommands":["DECIDE","CLOSE"]}}}}"""
        .Replace("FIELDS", fields, StringComparison.Ordinal)

let jsonEqual left right =
    use first = JsonDocument.Parse(ReadOnlyMemory<byte>(left))
    use second = JsonDocument.Parse(ReadOnlyMemory<byte>(right))

    Expect.isTrue
        (JsonElement.DeepEquals(first.RootElement, second.RootElement))
        "Wire meaning survives typed round-trip"

let moduleType =
    typeof<EndpointDescription>.Assembly.GetType("ClaimCore.Protocol.WebV2")
    |> nonNull

let responseCodec identifier =
    let property =
        moduleType.GetProperties(BindingFlags.Public ||| BindingFlags.Static)
        |> Array.find (fun property ->
            let description = property.PropertyType.GetProperty("Description")

            if isNull description then
                false
            else
                let binding = property.GetValue(null)

                let value =
                    (nonNull description).GetValue(binding)
                    |> nonNull
                    |> unbox<EndpointDescription>

                value.Identifier = identifier)

    let binding = property.GetValue(null) |> nonNull

    property.PropertyType.GetProperty("Response")
    |> nonNull
    |> fun p -> p.GetValue(binding) |> nonNull

let runCodec (codec: obj) bytes =
    let methodInfo = codec.GetType().GetMethod("Decode") |> nonNull
    let result = methodInfo.Invoke(codec, [| box maximum; box bytes |]) |> nonNull
    let case, values = FSharpValue.GetUnionFields(result, methodInfo.ReturnType)

    if case.Name = "Error" then
        None
    else
        let write = codec.GetType().GetMethod("Encode") |> nonNull
        let encoded = write.Invoke(codec, [| box maximum; values[0] |]) |> nonNull
        let outcome, payload = FSharpValue.GetUnionFields(encoded, write.ReturnType)
        Expect.equal outcome.Name "Ok" "Accepted typed value can be re-encoded"
        Some(unbox<byte array> payload[0])

let corpus () =
    let path =
        Path.Combine(
            RepositoryRoot.find (),
            "web/src/generated/convergence/web-v2.parsed-value-corpus.json"
        )

    use document = JsonDocument.Parse(File.ReadAllBytes path |> ReadOnlyMemory<byte>)

    document.RootElement.GetProperty("cases").EnumerateArray()
    |> Seq.map _.Clone()
    |> Seq.toList
