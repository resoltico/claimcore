module internal ClaimCore.AcceptanceTests.CliNativeOutput

open ClaimCore.Cli

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open CliCalls

let private run
    (fixture: PublishedFixture.Fixture)
    (environment: IReadOnlyDictionary<string, string>)
    command
    destination
    source
    : ProcessRunner.Result =
    let directory = Path.Combine(fixture.Directory, "native-output-" + id ())
    Directory.CreateDirectory directory |> ignore

    let result =
        ClaimCore.TestSupport.OutputControls.run
            directory
            fixture.Cli
            command
            {
                Destination = destination
                Environment = environment
            }
            source

    {
        ExitCode = result.ExitCode
        StandardOutput = result.StandardOutput
        StandardError = result.StandardError
    }

let private authored (fixture: PublishedFixture.Fixture) =
    let request =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = request["input"] |> nonNull
    let operation = id ()
    let reference = "NATIVE-OUTPUT-" + id ()
    input["operationId"] <- JsonValue.Create operation
    (input["target"] |> nonNull)["caseReference"] <- JsonValue.Create reference
    let values = (input["act"] |> nonNull)["values"] |> nonNull
    values["claimantName"] <- JsonValue.Create "Rīga مطالبة 😀"
    Encoding.UTF8.GetBytes(request.ToJsonString()), operation, reference

let private head (fixture: PublishedFixture.Fixture) =
    let result = fixture.Administration [ "head" ]
    Expect.equal result.ExitCode 0 "Authoritative head is available"
    use document = JsonDocument.Parse result.StandardOutput
    document.RootElement.GetProperty("position").GetProperty("sequence").GetInt64()

let private accepted fixture reference =
    let current =
        automation
            fixture
            "case.get"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = reference
                    |}
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal (current.GetProperty("revision").GetInt64()) 1L "One accepted case revision"

    let history =
        automation
            fixture
            "case.timeline"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = reference
                    |}
                filter = "FACTS"
                limit = 50
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal (history.GetProperty("items").GetArrayLength()) 1 "Exactly one accepted operation"

let private fallback (operation: string) (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode 4 "Undeliverable mutation confirmation remains uncertain"
    let lines = Encoding.UTF8.GetString(result.StandardError).TrimEnd('\n').Split('\n')
    Expect.equal lines.Length 3 "Fallback reply, guidance, and exact request frame"
    Expect.stringContains lines[1] "claimcore call" "Exact resending guidance"
    Expect.stringContains lines[1] "claimcore operation get" "Final lookup guidance"

    let frame =
        Frames.decode (Encoding.UTF8.GetBytes lines[0])
        |> Result.defaultWith (fun _ -> failtest "Fallback uses the shared typed frame codec")

    Expect.equal frame.Delivery DeliveryState.Uncertain "No false confirmation"
    Expect.equal frame.OperationId (Some(Guid.Parse operation)) "Original caller identity retained"

    let retry =
        Frames.admit (Encoding.UTF8.GetBytes lines[2])
        |> Result.defaultWith (fun _ -> failtest "Last stderr line is a retryable request")

    Expect.equal (Frames.operationId retry) frame.OperationId "Exact retry identity retained"

let private mutations fixture =
    let destinations =
        [ "broken"; "both" ] @ (if OperatingSystem.IsLinux() then [ "full" ] else [])

    for command in [ "call"; "session" ] do
        for destination in destinations do
            let source, operation, reference = authored fixture
            let later, _, laterReference = authored fixture

            let input =
                if command = "session" then
                    Array.concat [ source; [| 10uy |]; later; [| 10uy |] ]
                else
                    source

            let before = head fixture
            let result = run fixture fixture.Environment command destination input

            if destination = "both" then
                Expect.equal result.ExitCode 4 "Both broken sinks still leave exit four"
                Expect.isEmpty result.StandardError "No writable confirmation channel"
            else
                fallback operation result

            Expect.equal
                (head fixture)
                (before + 1L)
                "Output failure cannot dispatch a later session frame"

            accepted fixture reference

            if command = "session" then
                automation
                    fixture
                    "case.get"
                    {|
                        target =
                            {|
                                kind = "REFERENCE"
                                caseReference = laterReference
                            |}
                    |}
                    2
                |> require "REJECTED"
                |> ignore

    Expect.equal
        (fixture.Administration [ "verify" ]).ExitCode
        0
        "Ledger and projections independently reconstruct"

let private noMutation fixture =
    let before = head fixture

    for destination in [ "broken"; "both" ] do
        let result =
            run fixture fixture.Environment "call" destination (frame "context" {| |})

        Expect.isTrue
            (result.ExitCode <> 0 && result.ExitCode <> 4)
            "Read output failure is nonzero without uncertain mutation"

        if destination = "broken" then
            use document = JsonDocument.Parse result.StandardError

            Expect.equal
                (document.RootElement.GetProperty("endpoint").GetString())
                "context"
                "Read failure stays a read"

    let missing = Dictionary<string, string>()
    let result = run fixture missing "call" "broken" (frame "context" {| |})

    Expect.isTrue
        (result.ExitCode <> 0 && result.ExitCode <> 4)
        "Predispatch failure never becomes uncertain acceptance"

    for command, destination in
        [
            "help", "broken"
            "version", "broken"
            "unsupported.synthetic", "stderr-broken"
        ] do
        Expect.isTrue
            ((run fixture missing command destination [||]).ExitCode <> 0)
            "Offline sink failures cannot report successful delivery"

    Expect.equal (head fixture) before "Read/predispatch/offline controls append nothing"

let private unicodeFrames fixture =
    let source, _, reference = authored fixture
    CliCalls.run fixture fixture.Environment source 0 |> ignore

    let request =
        frame
            "case.get"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = reference
                    |}
            |}

    let result =
        CliCalls.invoke
            fixture
            fixture.Environment
            [ "session" ]
            (Array.concat [ request; [| 10uy |]; request; [| 10uy |] ])
            0

    Expect.equal result.ExitCode 0 "Normal multiframe session succeeds"

    let rows =
        Encoding.UTF8
            .GetString(result.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)

    Expect.equal rows.Length 2 "One complete newline-delimited frame per read"

    let stableRead (text: string) =
        let frame = JsonNode.Parse(text) |> nonNull

        let basis =
            [ "result"; "outcome"; "data"; "work"; "basis" ]
            |> List.fold (fun (current: JsonNode) key -> current[key] |> nonNull) frame

        Expect.isTrue
            (basis.AsObject().Remove "observedAt")
            "Each read declares its own database observation"

        frame.ToJsonString()

    Expect.equal
        (stableRead rows[0])
        (stableRead rows[1])
        "Repeated frames preserve all Unicode facts across separate read observations"

    Expect.equal
        result.StandardOutput[result.StandardOutput.Length - 1]
        10uy
        "Final newline is retained"

    for row in rows do
        Expect.isOk
            (Frames.decode (Encoding.UTF8.GetBytes row))
            "Unicode machine bytes remain admitted"

let private humanActorData (fixture: PublishedFixture.Fixture) =
    let subject =
        "synthetic-\u001B[31m\r\n\t\b\u0085\u202Eالعربية\u200C\u200D-literal\\u{001B}"

    let operation = id ()
    let owner = interactiveEnvironment fixture "synthetic-owner" "valid"

    let registered =
        CliCalls.verb
            fixture
            owner
            [
                "authority"
                "register"
                "--operation-id"
                operation
                "--principal-kind"
                "human"
                "--issuer"
                fixture.Issuer
                "--subject"
                subject
                "--name"
                "Safety"
            ]
            0
        |> require "ACCEPTED"

    let target = registered.GetProperty("subject").GetProperty "targetActor"

    Expect.equal
        (target.GetProperty("kind").GetString())
        "DISCLOSED"
        "Current owner receives the authorized target reference"

    let targetActor = target.GetProperty "actor"
    let actorId = targetActor.GetProperty("actorId").GetString() |> nonNull

    Expect.equal
        actorId
        operation
        "Registration keeps its deterministic operation-derived account identity"

    Expect.equal
        (targetActor.GetProperty("displayName").GetProperty("value").GetString())
        "Safety"
        "Authored profile owns the readable account name"

    let machine =
        CliCalls.verb fixture owner [ "authority"; "grants"; actorId; "--limit"; "50" ] 0
        |> require "SUCCEEDED"

    let found = machine.GetProperty "actor"

    Expect.equal
        (found.GetProperty("actor").GetProperty("actorId").GetString())
        actorId
        "Technical disclosure is for the already-known registered identity"

    Expect.isTrue
        (found.GetProperty("principal").GetProperty("subject").GetString() = subject)
        "The real service and JSON CLI preserve the exact principal identity"

    let listEnvironment = Dictionary<string, string>(owner)
    listEnvironment["COLUMNS"] <- "100"

    let primary =
        CliCalls.invoke
            fixture
            listEnvironment
            [ "authority"; "list"; "--limit"; "50"; "--format"; "text" ]
            Array.empty
            0

    let primaryText = Encoding.UTF8.GetString primary.StandardOutput

    let primaryRows = primaryText.Split('\n')

    let nameFirst =
        primaryRows
        |> Array.exists (fun line -> line.StartsWith("Safety  ", StringComparison.Ordinal))

    let identityFirst =
        primaryRows
        |> Array.exists (fun line ->
            line.StartsWith("synthetic-", StringComparison.Ordinal)
            || line.StartsWith(actorId, StringComparison.Ordinal))

    if not nameFirst || identityFirst then
        FixtureFiles.write fixture.Directory "account-primary-name.stdout" primary.StandardOutput
        |> ignore

    Expect.isTrue nameFirst "Primary rows identify the account by its exact authored name"

    Expect.isFalse identityFirst "Neither the unsafe principal nor ID is the leading name"

    // Generic rendering consumes the supplied view, not a reconstructed principal card from data.
    fixture.TransformNextReply(
        "/api/authority.grants",
        fun bytes ->
            let root = JsonNode.Parse(bytes: byte array) |> nonNull

            (root["outcome"] |> nonNull)["view"] <-
                JsonSerializer.SerializeToNode
                    {|
                        regions =
                            [|
                                {|
                                    role = "DETAILS"
                                    titleId = "label.recordDetails"
                                    content =
                                        {|
                                            kind = "ENTRIES"
                                            entries =
                                                [|
                                                    {|
                                                        key = "technical-subject"
                                                        labelId = "field.principal.subject.label"
                                                        prominence = "DETAIL"
                                                        value =
                                                            {|
                                                                kind = "IDENTIFIER"
                                                                value = subject
                                                            |}
                                                    |}
                                                |]
                                        |}
                                |}
                            |]
                    |}

            Encoding.UTF8.GetBytes(root.ToJsonString())
    )

    let rendered =
        CliCalls.invoke
            fixture
            owner
            [
                "authority"
                "grants"
                actorId
                "--limit"
                "50"
                "--format"
                "text"
                "--details"
            ]
            Array.empty
            0

    let text = Encoding.UTF8.GetString rendered.StandardOutput

    let row =
        text.Split('\n')
        |> Array.find (fun line -> line.Contains("synthetic-", StringComparison.Ordinal))

    for control in [ '\u001B'; '\r'; '\t'; '\b'; '\u0085'; '\u202E' ] do
        Expect.isFalse
            (primaryText.Contains control || text.Contains control)
            "Neither human table nor technical card emits untrusted terminal instructions"

    Expect.isTrue
        (row.Contains(
            "\\u{001B}[31m\\u{000D}\\u{000A}\\u{0009}\\u{0008}\\u{0085}\\u{202E}",
            StringComparison.Ordinal
        ))
        "Independent visible terminal escape profile"

    Expect.isTrue
        (row.Contains("العربية\u200C\u200D", StringComparison.Ordinal))
        "Arabic and joining characters remain readable"

    Expect.isTrue
        (row.Contains("literal\\\\u{001B}", StringComparison.Ordinal))
        "Literal escape-like strings remain distinguishable"

let qualify fixture =
    let source, _, reference = authored fixture
    CliNarrativeOutput.qualify fixture source reference (fun () -> head fixture)
    humanActorData fixture
    mutations fixture
    noMutation fixture
    unicodeFrames fixture

let publishedOutput =
    let run () =
        task {
            use fixture = new PublishedFixture.Fixture()
            do! fixture.Start()
            setup fixture
            qualify fixture
            fixture.Complete()
        }

    testCaseAsync
        "published CLI output preserves supplied screens and terminal safety"
        (async { do! run () |> Async.AwaitTask })
