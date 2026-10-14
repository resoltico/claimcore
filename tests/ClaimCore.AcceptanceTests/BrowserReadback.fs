module internal ClaimCore.AcceptanceTests.BrowserReadback

open System
open System.IO
open System.Text.Json
open Expecto
open CliCalls

let qualify fixture output =
    use document =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "unicode-case.json")))

    let authored = document.RootElement
    let reference = authored.GetProperty("reference").GetString() |> nonNull
    let operationId = authored.GetProperty("operationId").GetString() |> nonNull

    let current =
        automation fixture "case.get" {| caseReference = reference |} 0
        |> require "SUCCEEDED"
        |> fun value -> value.GetProperty("current").GetProperty("case")

    Expect.equal (current.GetProperty("revision").GetString()) "1" "One browser acceptance"
    let fields = current.GetProperty("fields")

    for field, expected in
        [
            "claimantName", String.replicate 200 "🙂"
            "insurerName", String.replicate 200 "🙂"
            "incidentCountry", String.replicate 100 "🙂"
        ] do
        Expect.equal
            (fields.GetProperty(field).GetString())
            expected
            "Exact native Unicode readback"

    let receipt =
        automation fixture "operation.observe" {| operationId = operationId |} 0
        |> require "SUCCEEDED"
        |> fun value -> value.GetProperty("receipt")

    Expect.equal (receipt.GetProperty("caseReference").GetString()) reference "Exact reference"
    Expect.equal (receipt.GetProperty("operationId").GetString()) operationId "Browser operation"

    Expect.equal
        (receipt.GetProperty("snapshot").GetRawText())
        (current.GetRawText())
        "Same snapshot"
