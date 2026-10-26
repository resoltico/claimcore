module ClaimCore.Tests.OpenApiConformanceTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open Microsoft.OpenApi
open global.Json.Schema
open ClaimCore.Contract
open ClaimCore.Json
open ClaimCore.Contract.Protocol
open ClaimCore.TestSupport

let private emitted () =
    ClaimCore.Contract.OpenApi.document
    |> WireWriter.bytes
    |> Encoding.UTF8.GetString

let private parser () =
    let result = OpenApiDocument.Parse(emitted (), "json")

    Expect.isEmpty
        (nonNull result.Diagnostic).Errors
        "Independent OpenAPI parser admits the complete document"

    Expect.equal
        ((nonNull result.Document).Paths.Keys |> Set.ofSeq)
        (Api.all |> List.map Api.path |> Set.ofList)
        "Exactly the current registry paths are present"

    let broken = OpenApiDocument.Parse("{\"openapi\":\"3.1.0\",\"paths\":{}}", "json")

    Expect.isNonEmpty
        (nonNull broken.Diagnostic).Errors
        "Missing required info is a rejecting control"

let private child (name: string) (node: JsonNode) = node[name] |> nonNull

let private schema (document: JsonNode) endpoint response =
    let op =
        document
        |> child "paths"
        |> child (Api.path endpoint)
        |> child (((Api.method endpoint).Method).ToLowerInvariant())

    let selected =
        if response then
            op |> child "responses" |> child "200"
        else
            op |> child "requestBody"
        |> child "content"
        |> child "application/json"
        |> child "schema"

    let root = JsonObject()
    root["$schema"] <- JsonValue.Create "https://json-schema.org/draft/2020-12/schema"
    root["components"] <- (document |> child "components").DeepClone()
    root["allOf"] <- JsonArray(selected.DeepClone())
    JsonSchema.FromText(root.ToJsonString(), BuildOptions(Dialect = Dialect.Draft202012))

let private fixture name =
    File.ReadAllText(Path.Combine(RepositoryRoot.find (), "tests/fixtures", name))
    |> JsonDocument.Parse

let private evaluate (schema: JsonSchema) (value: JsonElement) =
    schema.Evaluate(value, EvaluationOptions(RequireFormatValidation = true)).IsValid

let private corpus response filename () =
    let document = JsonNode.Parse(emitted ()) |> nonNull
    use examples = fixture filename
    let mutable endpoints = Set.empty

    for example in examples.RootElement.GetProperty("cases").EnumerateArray() do
        let endpoint =
            example.GetProperty("endpoint").GetString()
            |> nonNull
            |> Api.tryFind
            |> Option.get

        if response || Api.id endpoint <> "session" then
            let selected = schema document endpoint response
            let value = example.GetProperty "value"

            let expected =
                match example.TryGetProperty "valid" with
                | true, valid -> valid.GetBoolean()
                | _ -> true

            Expect.equal
                (evaluate selected value)
                expected
                "Independent validator judges the frozen example."

            let altered = JsonNode.Parse(value.GetRawText()) |> nonNull
            altered["unexpectedMember"] <- JsonValue.Create true
            use invalid = JsonDocument.Parse(altered.ToJsonString())

            Expect.isFalse
                (evaluate selected invalid.RootElement)
                "Closed schema rejects an added member"

            endpoints <- Set.add (Api.id endpoint) endpoints

    let expected =
        Api.all
        |> List.filter (fun endpoint ->
            response || Api.method endpoint = System.Net.Http.HttpMethod.Post)
        |> List.map Api.id
        |> Set.ofList

    Expect.equal endpoints expected "Corpus covers each endpoint with a request body or response"

let private validatorControls () =
    let constrained =
        JsonSchema.FromText(
            "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"required\":[\"value\"],\"properties\":{\"value\":{\"type\":\"integer\",\"minimum\":1}},\"additionalProperties\":false}"
        )

    for text in [ "{}"; "{\"value\":0}"; "{\"value\":\"1\"}"; "{\"value\":1,\"extra\":true}" ] do
        use value = JsonDocument.Parse text

        Expect.isFalse
            (evaluate constrained value.RootElement)
            "Validator rejects required, bound, type and closed-object violations"

let private sharedResponse (document: JsonNode) (reference: JsonNode) =
    let pointer = reference |> child "$ref" |> fun value -> value.GetValue<string>()
    let prefix = "#/components/responses/"

    Expect.isTrue
        (pointer.StartsWith(prefix, StringComparison.Ordinal))
        "Host failure uses a local response reference"

    document
    |> child "components"
    |> child "responses"
    |> child (pointer.Substring(prefix.Length))

let private responseStatuses () =
    let document = JsonNode.Parse(emitted ()) |> nonNull
    let statuses = HostFailures.all |> List.map HostFailures.status |> List.distinct

    let responses =
        document
        |> child "paths"
        |> child "/api/context"
        |> child "post"
        |> child "responses"

    for status in statuses do
        let selected =
            responses
            |> child (string status)
            |> sharedResponse document
            |> child "content"
            |> child "application/json"
            |> child "schema"

        let root = JsonObject()
        root["components"] <- (document |> child "components").DeepClone()
        root["allOf"] <- JsonArray(selected.DeepClone())

        let validator =
            JsonSchema.FromText(root.ToJsonString(), BuildOptions(Dialect = Dialect.Draft202012))

        for reason in HostFailures.all do
            use body = JsonDocument.Parse(HostFailureCodecs.encode reason)

            Expect.equal
                (evaluate validator body.RootElement)
                (HostFailures.status reason = status)
                "HTTP response status permits exactly its fixed typed failure policies."

let private securityRequirements () =
    use document = JsonDocument.Parse(emitted ())

    let schemes =
        document.RootElement.GetProperty("components").GetProperty("securitySchemes")

    let csrf = schemes.GetProperty("antiforgery")
    Expect.equal (csrf.GetProperty("type").GetString()) "apiKey" "CSRF is a separate credential."
    Expect.equal (csrf.GetProperty("in").GetString()) "header" "CSRF travels in a header."

    Expect.equal
        (csrf.GetProperty("name").GetString())
        "X-ClaimCore-Antiforgery"
        "The named header matches host admission."

    for endpoint in Api.all do
        let operation =
            document.RootElement
                .GetProperty("paths")
                .GetProperty(Api.path endpoint)
                .GetProperty(((Api.method endpoint).Method).ToLowerInvariant())

        let actual =
            operation.GetProperty("security").EnumerateArray()
            |> Seq.map (fun requirement ->
                requirement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
            |> Set.ofSeq

        let expected =
            match Api.id endpoint with
            | "session" -> [ Set.empty; Set.singleton "cookieAuth"; Set.singleton "bearerAuth" ]
            | "session.logout" -> [ Set.ofList [ "cookieAuth"; "antiforgery" ] ]
            | _ -> [ Set.ofList [ "cookieAuth"; "antiforgery" ]; Set.singleton "bearerAuth" ]

        Expect.equal
            actual
            (Set.ofList expected)
            "Cookie and bearer alternatives have endpoint-specific CSRF requirements."

let private sharedFailures () =
    use bytes = JsonDocument.Parse(emitted ())
    let minified = JsonSerializer.SerializeToUtf8Bytes bytes.RootElement
    Expect.isLessThanOrEqual minified.Length 80000 "The minified contract remains within 80 KB"
    let document = JsonNode.Parse(emitted ()) |> nonNull

    let expected =
        HostFailures.all |> List.map (HostFailures.status >> string) |> Set.ofList

    for path in (document |> child "paths").AsObject() do
        for operation in (nonNull path.Value).AsObject() do
            let responses = nonNull operation.Value |> child "responses" |> _.AsObject()

            Expect.equal
                (responses |> Seq.map _.Key |> Set.ofSeq)
                (Set.add "200" expected)
                "Every endpoint retains the failure statuses"

            for status in expected do
                let reference = responses[status] |> nonNull

                Expect.equal
                    (reference.AsObject().Count)
                    1
                    "Host responses contain only the reference"

                sharedResponse document reference |> ignore

[<Tests>]
let tests =
    testList
        "independent OpenAPI and JSON Schema validation"
        [
            testCase "OpenAPI 3.1 parser accepts inventory and rejects incomplete document" parser
            testCase
                "request corpus validates independently with rejecting controls"
                (corpus false "wire-request-examples.json")
            testCase
                "response corpus validates independently with rejecting controls"
                (corpus true "wire-response-examples.json")
            testCase "JSON Schema 2020-12 validator enforces negative controls" validatorControls
            testCase "HTTP failure schemas bind their declared status" responseStatuses
            testCase "host failures share responses within the size budget" sharedFailures
            testCase
                "OpenAPI security alternatives preserve cookie CSRF and bearer admission"
                securityRequirements
        ]
