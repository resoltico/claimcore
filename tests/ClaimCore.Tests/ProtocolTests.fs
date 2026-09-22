module ClaimCore.Tests.ProtocolTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Cli
open ClaimCore.Contracts
open ClaimCore.Domain
open ClaimCore.TestSupport

let private bytes value = Encoding.UTF8.GetBytes(value: string)

let private rawCorpusPath =
    Path.Combine(
        RepositoryRoot.find (),
        "web/src/generated/convergence/cli-v3.raw-decoder-corpus.json"
    )

let private parse text =
    match StrictJson.parseDocument 131072 (bytes text) with
    | Ok document -> document
    | Error problem -> failtestf "Expected strict JSON: %s" problem.Code

let private invocation endpoint input =
    $"{{\"protocolVersion\":3,\"endpoint\":\"{endpoint}\",\"input\":{input}}}"

let private openInput =
    """{"operationId":"20000000-0000-4000-8000-000000000401","caseReference":"CLI-001","expectedRevision":"0","command":{"kind":"OPEN","values":{"incidentDate":"2026-08-01","incidentNotificationDate":"2026-08-03","incidentCountry":"Example Country","claimantName":"Example Claimant Ltd","insurerName":"Example Insurer","claimedAmount":"1000.00","claimedCurrency":"EUR"}}}"""

let private expectInvocationFailure source =
    use document = parse (invocation "command.prepare" source)
    Expect.isError (InvocationFraming.decode document.RootElement) "Invalid command input"

let private strictScalarAndContainerRejections () =
    expectInvocationFailure (openInput.Replace("\"1000.00\"", "1000"))
    expectInvocationFailure (openInput.Replace("\"Example Claimant Ltd\"", "null"))

    expectInvocationFailure
        """{"operationId":"20000000-0000-4000-8000-000000000401","caseReference":"CLI-001","expectedRevision":"0","command":[]}"""

let private contractTests =
    testList
        "CLI v3 generated contract"
        [
            testCase
                "endpoint implementation inventory is exactly the generated CLI catalog"
                (fun () ->
                    let generated =
                        (ContractProjection.current ()).CliEndpoints |> List.map _.Identifier

                    let implemented = Endpoint.all |> List.map Endpoint.identifier
                    Expect.equal implemented generated "No hand-maintained endpoint vocabulary")
            testCase
                "generated schemas are strict JSON documents with the v3 declaration"
                (fun () ->
                    let model = ContractProjection.current ()

                    for schema in
                        [
                            CliSchemas.invocation model
                            CliSchemas.response model
                            CliSchemas.definition model
                        ] do
                        use document = JsonDocument.Parse(CanonicalContract.bytes schema)

                        Expect.equal
                            document.RootElement.ValueKind
                            JsonValueKind.Object
                            "Schema object"

                        Expect.equal
                            (document.RootElement.GetProperty("$schema").GetString())
                            "https://json-schema.org/draft/2020-12/schema"
                            "Current JSON Schema draft")
        ]

let private acceptsDraft =
    testCase "accepts a typed command draft with string revision and exact values" (fun () ->
        use document = parse (invocation "command.prepare" openInput)

        match InvocationFraming.decode document.RootElement with
        | Ok(Endpoint.CommandPrepare, EndpointInput.Draft draft, None) ->
            Expect.equal draft.ExpectedVersion 0L "Canonical decimal revision"

            match draft.Command with
            | DraftCommand.Flat(CommandKind.Open, values) ->
                Expect.equal values.Length 7 "Exact OPEN field set"
            | _ -> failtest "Expected the typed OPEN flat command shape."
        | _ -> failtest "Expected decoded command.prepare draft."

        strictScalarAndContainerRejections ())

let private rejectedVersions =
    testList
        "protocol and revision rejection"
        [
            testCase "rejects protocol v2 before endpoint execution" (fun () ->
                use document =
                    parse (
                        invocation "command.prepare" openInput
                        |> _.Replace("\"protocolVersion\":3", "\"protocolVersion\":2")
                    )

                match InvocationFraming.decode document.RootElement with
                | Error problem -> Expect.equal problem.Code "INVALID_RANGE" "Hard protocol break"
                | Ok _ -> failtest "Protocol v2 must not be decoded.")
            testCase
                "rejects a numeric revision rather than risking JavaScript precision loss"
                (fun () ->
                    let changed =
                        invocation "command.prepare" openInput
                        |> _.Replace("\"expectedRevision\":\"0\"", "\"expectedRevision\":0")

                    use document = parse changed

                    match InvocationFraming.decode document.RootElement with
                    | Error problem ->
                        Expect.equal problem.Code "INVALID_SHAPE" "Revision stays decimal text"
                    | Ok _ -> failtest "Numeric revision must not be coerced.")
        ]

let private optionalText (name: string) (value: JsonElement) : string option =
    let property = value.GetProperty(name)

    if property.ValueKind = JsonValueKind.Null then
        None
    else
        property.GetString() |> Option.ofObj

let private decodeRaw bytes =
    match StrictJson.parseDocument 131072 bytes with
    | Error problem -> Error problem
    | Ok document ->
        use source = document

        InvocationFraming.decode source.RootElement
        |> Result.map (fun (endpoint, _, _) -> endpoint)

let private rawCorpus () =
    use document = JsonDocument.Parse(File.ReadAllBytes(rawCorpusPath))
    let root = document.RootElement
    Expect.equal (root.GetProperty("schemaVersion").GetInt32()) 1 "Raw corpus version"

    let cases = root.GetProperty("cases").EnumerateArray() |> Seq.toList
    let identifiers = cases |> List.map (fun item -> item.GetProperty("id").GetString())
    Expect.equal identifiers.Length (identifiers |> Set.ofList |> Set.count) "Unique corpus IDs"

    let mutable validEndpoints = []

    for item in cases do
        let identifier =
            item.GetProperty("id").GetString() |> Option.ofObj |> Option.defaultValue ""

        let source =
            item.GetProperty("bytesBase64").GetString()
            |> Option.ofObj
            |> Option.defaultValue ""
            |> Convert.FromBase64String

        match item.GetProperty("valid").GetBoolean(), decodeRaw source with
        | true, Ok endpoint ->
            Expect.isNone (optionalText "expectedCode" item) (identifier + " has no error code")
            Expect.isNone (optionalText "expectedPath" item) (identifier + " has no error path")
            validEndpoints <- Endpoint.identifier endpoint :: validEndpoints
        | false, Error problem ->
            Expect.equal (Some problem.Code) (optionalText "expectedCode" item) identifier
            Expect.equal (Some problem.Path) (optionalText "expectedPath" item) identifier
        | true, Error problem -> failtestf "%s unexpectedly failed with %s" identifier problem.Code
        | false, Ok _ -> failtestf "%s unexpectedly decoded" identifier

    let expected =
        (ContractProjection.current ()).CliEndpoints
        |> List.map _.Identifier
        |> Set.ofList

    Expect.equal (Set.ofList validEndpoints) expected "Every generated CLI endpoint has valid bytes"

let private privacyFailure raw =
    match StrictJson.parseDocument 131072 (bytes raw) with
    | Error problem -> problem
    | Ok document ->
        use frame = document

        match InvocationFraming.decode frame.RootElement with
        | Error problem -> problem
        | Ok _ -> failtest "The privacy probe must be refused."

let private privateErrorPaths =
    testCase "unknown and duplicate property paths never echo authored key names" (fun () ->
        let canary = "SECRET_CLAIMANT_CANARY"

        let unknown =
            """{"protocolVersion":3,"endpoint":"case.list","input":{"limit":1,"SECRET_CLAIMANT_CANARY":"value"}}"""

        let duplicate =
            """{"protocolVersion":3,"endpoint":"case.list","input":{"SECRET_CLAIMANT_CANARY":{"x":1,"x":2},"limit":1}}"""

        let unknownFailure = privacyFailure unknown
        let duplicateFailure = privacyFailure duplicate
        Expect.equal unknownFailure.Code "UNKNOWN_PROPERTY" "Unknown key classification"
        Expect.equal unknownFailure.Path "/input" "Only the registered parent path is returned"
        Expect.equal duplicateFailure.Code "DUPLICATE_KEY" "Nested duplicate classification"
        Expect.equal duplicateFailure.Path "" "Unknown nesting names are not reflected"

        for problem in [ unknownFailure; duplicateFailure ] do
            Expect.isFalse (problem.Path.Contains(canary)) "No authored key in the path"

            Expect.isFalse
                ((ClaimCore.Contracts.ProtocolProblems.render problem.Reason).Contains(canary))
                "No authored key in the message")

let private missingRequiredInput =
    testCase "missing required endpoint input is rejected before runtime access" (fun () ->
        let source = """{"protocolVersion":3,"endpoint":"case.get","input":{}}"""
        use document = parse source

        match InvocationFraming.decode document.RootElement with
        | Error problem ->
            Expect.equal problem.Code "MISSING_PROPERTY" "Required case reference"
            Expect.equal problem.Path "/input/caseReference" "Registered field path"
        | Ok _ -> failtest "A missing endpoint input must not reach the runtime.")

let private timeoutAndParser =
    testList
        "timeout and strict parser rejection"
        [
            testCase "forbids caller timeouts for mutation endpoints" (fun () ->
                let frame =
                    $"{{\"protocolVersion\":3,\"endpoint\":\"command.execute\",\"input\":{openInput},\"timeoutMs\":100}}"

                use document = parse frame

                match InvocationFraming.decode document.RootElement with
                | Error problem ->
                    Expect.equal problem.Code "TIMEOUT_FORBIDDEN" "No mutation cancellation"
                | Ok _ -> failtest "Mutation timeout must be rejected.")
            testCase "strict parser rejects duplicate frame keys and invalid UTF-8" (fun () ->
                let duplicate =
                    """{"protocolVersion":3,"protocolVersion":3,"endpoint":"case.list","input":{"limit":1}}"""

                Expect.isError
                    (StrictJson.parseDocument 131072 (bytes duplicate))
                    "No duplicate-key last-wins"

                let nestedDuplicate =
                    """{"protocolVersion":3,"endpoint":"command.prepare","input":{"operationId":"20000000-0000-4000-8000-000000000401","caseReference":"CLI-001","expectedRevision":"0","command":{"kind":"CLOSE","kind":"CLOSE","values":{}}}}"""

                Expect.isError
                    (StrictJson.parseDocument 131072 (bytes nestedDuplicate))
                    "Nested duplicate keys are rejected"

                Expect.isError (StrictJson.parseDocument 131072 [| 0xFFuy |]) "Invalid UTF-8")
            testCase
                "generated raw corpus exercises every endpoint and strict rejection class"
                rawCorpus
            missingRequiredInput
            privateErrorPaths
        ]

let private framingTests =
    testList "CLI v3 framing" [ acceptsDraft; rejectedVersions; timeoutAndParser ]

let tests = testList "CLI v3 protocol" [ contractTests; framingTests ]
