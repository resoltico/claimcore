module ClaimCore.Tests.MessageTemplateTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.TestSupport

let private vectors () =
    File.ReadAllText(
        Path.Combine(RepositoryRoot.find (), "tests/fixtures/message-template-vectors.json")
    )
    |> JsonDocument.Parse

let private args (row: JsonElement) =
    row.GetProperty("arguments").EnumerateObject()
    |> Seq.map (fun property -> property.Name, property.Value.GetString() |> nonNull)
    |> Map.ofSeq

let private accepted () =
    use examples = vectors ()

    for row in examples.RootElement.GetProperty("accepted").EnumerateArray() do
        let source = row.GetProperty("source").GetString() |> nonNull
        let template = Template.parse source |> Fixtures.accepted
        let expected = row.GetProperty("expected").GetString() |> nonNull

        Expect.equal
            (Template.render template (args row))
            expected
            "Restricted ICU rendering matches the frozen independent example."

let private rejected () =
    use examples = vectors ()

    for row in examples.RootElement.GetProperty("rejected").EnumerateArray() do
        let source = row.GetString() |> nonNull

        Expect.isError
            (Template.parse source)
            "Malformed, unsupported and unsafe message text refuses."

    Expect.isError
        (Template.parse (String('x', 8001)))
        "Catalog messages retain their authored-text bound."

let private arguments () =
    let template =
        Template.parse "Request {operationId}: {result}." |> Fixtures.accepted

    Expect.equal
        (Template.arguments template)
        (Set.ofList [ "operationId"; "result" ])
        "Arguments are derived from the parsed template."

    for supplied in
        [
            Map.empty
            Map.ofList [ "operationId", "synthetic" ]
            Map.ofList [ "operationId", "synthetic"; "result", "accepted"; "extra", "value" ]
        ] do
        Expect.throws
            (fun () -> Template.render template supplied |> ignore)
            "Missing and extra producer arguments fail rather than silently degrading messages."

let private catalog () =
    let catalog =
        Messages.catalog [ "notice.accepted", "Accepted {operationId}." ]
        |> Fixtures.accepted

    Expect.equal
        (Messages.render catalog "notice.accepted" (Map.ofList [ "operationId", "synthetic" ]))
        "Accepted synthetic."
        "A host-admitted catalog renders without I/O."

    Expect.isError
        (Messages.catalog [ "notice.accepted", "first"; "notice.accepted", "second" ])
        "Duplicate authored keys refuse."

    Expect.isError
        (Messages.catalog [ "notice.invalid", "{count, number}" ])
        "Catalog admission validates every template."

    Expect.throws
        (fun () -> Messages.render catalog "notice.absent" Map.empty |> ignore)
        "Unknown message identities fail explicitly."

[<Tests>]
let tests =
    testList
        "restricted ICU message templates"
        [
            testCase "literal text, quoting and simple holes render" accepted
            testCase "unsupported syntax and unsafe literals refuse" rejected
            testCase "producer arguments exactly match template holes" arguments
            testCase "catalog admission rejects duplicate and invalid definitions" catalog
        ]
