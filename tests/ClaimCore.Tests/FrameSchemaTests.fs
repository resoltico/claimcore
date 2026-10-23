module ClaimCore.Tests.FrameSchemaTests

open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open global.Json.Schema
open ClaimCore.Contract
open ClaimCore.TestSupport

let private schema shape =
    let root =
        JsonNode.Parse(WireWriter.bytes (SchemaJson.value "#/components/schemas/" shape))
        |> nonNull

    let definitions = JsonObject()

    for KeyValue(name, value) in CliSchemas.definitions do
        definitions[name] <-
            JsonNode.Parse(WireWriter.bytes (SchemaJson.value "#/components/schemas/" value))

    let components = JsonObject()
    components["schemas"] <- definitions
    root["components"] <- components
    JsonSchema.FromText(root.ToJsonString(), BuildOptions(Dialect = Dialect.Draft202012))

let private examples response () =
    let direction = if response then "response" else "request"

    let selected =
        schema (
            if response then
                CliSchemas.response
            else
                CliSchemas.invocation
        )

    let path =
        Path.Combine(RepositoryRoot.find (), "tests/fixtures/wire-" + direction + "-examples.json")

    use fixture = JsonDocument.Parse(File.ReadAllBytes path)

    for example in fixture.RootElement.GetProperty("cases").EnumerateArray() do
        let frame = JsonObject()
        frame["endpoint"] <- JsonValue.Create(example.GetProperty("endpoint").GetString())
        let value = example.GetProperty("value")

        if response then
            frame["operationId"] <- null
            frame["delivery"] <- JsonValue.Create "DELIVERED"
            frame["result"] <- JsonNode.Parse(value.GetRawText())
        else
            frame["input"] <- JsonNode.Parse(value.GetRawText())

        let expected = example.GetProperty("valid").GetBoolean()
        let bytes = System.Text.Encoding.UTF8.GetBytes(frame.ToJsonString())

        let admitted =
            if response then
                Frames.decode bytes |> Result.isOk
            else
                Frames.admit bytes |> Result.isOk

        Expect.equal admitted expected "Actual frame admission judges the frozen payload."
        use document = JsonDocument.Parse bytes

        Expect.equal
            (selected.Evaluate(document.RootElement).IsValid)
            expected
            "Independent frame schema agrees."

        frame["unexpectedMember"] <- JsonValue.Create true
        use changed = JsonDocument.Parse(frame.ToJsonString())

        Expect.isFalse
            (selected.Evaluate(changed.RootElement).IsValid)
            "Frame schemas reject extra outer members."

[<Tests>]
let tests =
    testList
        "offline frame schemas"
        [
            testCase "invocations derive every registry input" (examples false)
            testCase "reply frames derive every registry output" (examples true)
        ]
