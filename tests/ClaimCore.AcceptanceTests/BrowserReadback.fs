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

    Expect.equal (current.GetProperty("revision").GetInt64()) 1L "One browser acceptance"
    let fields = current.GetProperty("state").GetProperty("fields")

    for field, expected in
        [
            "claimantName", String.replicate 200 "🙂"
            "insurerName", String.replicate 200 "🙂"
            "incidentCountry", "LT"
        ] do
        Expect.equal
            (fields.GetProperty(field).GetString())
            expected
            "Exact native Unicode readback"

    let receipt =
        automation fixture "operation.observe" {| operationId = operationId |} 0
        |> require "SUCCEEDED"

    Expect.equal (receipt.GetProperty("caseReference").GetString()) reference "Exact reference"
    Expect.equal (receipt.GetProperty("operationId").GetString()) operationId "Browser operation"

    Expect.equal
        (receipt.GetProperty("snapshot").GetProperty("state").GetProperty("fields").GetRawText())
        (fields.GetRawText())
        "Same snapshot"
