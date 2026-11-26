module internal ClaimCore.AcceptanceTests.CliRecovery

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Expecto
open ClaimCore.TestSupport
open CliCalls

let private listing (fixture: PublishedFixture.Fixture) =
    let result =
        ProcessRunner.dotnet
            60000
            fixture.Cli
            [ "recovery"; "list"; "--format"; "json" ]
            fixture.Environment
            None

    Expect.equal result.ExitCode 0 "Restarted recovery listing succeeds"
    use json = JsonDocument.Parse result.StandardOutput

    json.RootElement.GetProperty("recovery").EnumerateArray()
    |> Seq.map _.Clone()
    |> Seq.toList

let private submitted (fixture: PublishedFixture.Fixture) =
    let source =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = source["input"] |> nonNull
    let operation, reference = id (), "RECOVERY-PROCESS-" + (id ()).Substring(24)
    input["operationId"] <- JsonValue.Create operation

    (input["inputs"] |> nonNull)["field:caseReference"] <- JsonValue.Create reference

    Encoding.UTF8.GetBytes(source.ToJsonString()), operation, reference

let private acceptedEvidence (fixture: PublishedFixture.Fixture) operation =
    let path = Path.Combine(fixture.Directory, "redirect-evidence.json")
    let watch = Stopwatch.StartNew()
    let mutable found = false

    while not found && watch.Elapsed < TimeSpan.FromSeconds 15. do
        if File.Exists path then
            try
                use json = JsonDocument.Parse(File.ReadAllBytes path)
                let reply = json.RootElement.GetProperty "reply"

                found <-
                    reply.ValueKind = JsonValueKind.Object
                    && reply.GetProperty("outcome").GetProperty("kind").GetString() = "ACCEPTED"
                    && reply
                        .GetProperty("outcome")
                        .GetProperty("data")
                        .GetProperty("operationId")
                        .GetString()
                        =
                        operation
            with :? JsonException ->
                ()

        if not found then
            Thread.Sleep 20

    Expect.isTrue found "The real backend accepted while its reply remained withheld"

let private relativeRoot (fixture: PublishedFixture.Fixture) =
    let source, _, _ = submitted fixture
    let privatePath = "private-relative-recovery-canary"

    let environment =
        System.Collections.Generic.Dictionary<string, string>(fixture.Environment)

    environment["XDG_STATE_HOME"] <- privatePath

    let failed = 3

    let verify (result: ProcessRunner.Result) =
        let frame = parse failed result

        Expect.equal
            (frame.GetProperty("delivery").GetString())
            "NOT_SENT"
            "An invalid location cannot dispatch a write"

        let failure = frame.GetProperty("result").GetProperty("localFailure")

        Expect.equal
            (failure.GetProperty("code").GetString())
            "RECOVERY_ROOT_NOT_ABSOLUTE"
            "Location is not a storage failure"

        Expect.equal
            (failure.GetProperty("setting").GetString())
            "XDG_STATE_HOME"
            "Only the effective setting name is reported"

        Expect.isFalse
            (frame.GetRawText().Contains privatePath)
            "The frame does not expose the configured path"

    invoke fixture environment [ "call" ] source failed |> verify

    invoke fixture environment [ "recovery"; "list"; "--format"; "json" ] Array.empty failed
    |> verify

    let text =
        invoke
            fixture
            environment
            [ "recovery"; "list"; "--format"; "text"; "--language"; "en" ]
            Array.empty
            failed

    let wording = Encoding.UTF8.GetString text.StandardOutput
    Expect.stringContains wording "absolute directory path" "Recovery guidance describes the remedy"
    Expect.stringContains wording "XDG_STATE_HOME" "Recovery guidance identifies the setting"
    Expect.isFalse (wording.Contains privatePath) "Text does not expose the configured path"

    let blockedRoot =
        FixtureFiles.text fixture.Directory "recovery-root-not-a-directory" "Synthetic"

    environment["XDG_STATE_HOME"] <- blockedRoot
    let blocked = invoke fixture environment [ "call" ] source failed |> parse failed

    Expect.equal
        (blocked.GetProperty("delivery").GetString())
        "NOT_SENT"
        "Storage refusal cannot dispatch a write"

    Expect.equal
        (blocked.GetProperty("result").GetProperty("localFailure").GetProperty("code").GetString())
        "RECOVERY_UNAVAILABLE"
        "A real storage failure keeps its storage remedy"

/// Two actual processes share an operation UUID. Only the still-pending attempt survives a replay.
let qualify (fixture: PublishedFixture.Fixture) =
    relativeRoot fixture
    let source, operation, reference = submitted fixture

    JsonSerializer.Serialize
        {|
            id = id ()
            path = "/api/operation.execute"
            status = RedirectControl.heldResponse
        |}
    |> FixtureFiles.text fixture.Directory "redirect-control.json"
    |> ignore

    let start =
        PublishedProcesses.start "dotnet" [ fixture.Cli; "call" ] fixture.Environment

    CliProcessEnvironment.isolateProfile start (Path.GetDirectoryName fixture.Cli |> nonNull)
    start.RedirectStandardInput <- true
    use pending = Process.Start(start) |> nonNull
    let output = pending.StandardOutput.ReadToEndAsync()
    let errors = pending.StandardError.ReadToEndAsync()

    try
        pending.StandardInput.BaseStream.Write source
        pending.StandardInput.BaseStream.Flush()
        pending.StandardInput.Close()
        acceptedEvidence fixture operation

        let own =
            listing fixture
            |> List.filter (fun record -> record.GetProperty("operationId").GetString() = operation)

        Expect.hasLength own 1 "The pending process committed its identity before dispatch"

        let expectedKeys =
            Set.ofList
                [
                    "format"
                    "serviceOrigin"
                    "installationId"
                    "actorId"
                    "attemptId"
                    "operationId"
                    "task"
                    "refused"
                ]

        Expect.equal
            (own.Head.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
            expectedKeys
            "Only identity and delivery knowledge are retained"

        Expect.equal (own.Head.GetProperty("format").GetInt32()) 4 "Current recovery format"

        Expect.equal
            (own.Head.GetProperty("task").GetString())
            "case.open"
            "Exact declared task identity"

        Expect.isFalse
            (own.Head.GetRawText().Contains(reference, StringComparison.Ordinal))
            "The reference is absent"

        let replay = run fixture fixture.Environment source 0 |> require "ALREADY_ACCEPTED"

        Expect.equal
            (replay.GetProperty("operationId").GetString())
            operation
            "The second process gets the original receipt"

        let remaining =
            listing fixture
            |> List.filter (fun record -> record.GetProperty("operationId").GetString() = operation)

        Expect.equal
            (remaining |> List.map _.GetRawText())
            (own |> List.map _.GetRawText())
            "The second process cannot clear the first process's attempt"

        Expect.isFalse pending.HasExited "The process is still waiting for the withheld reply"
        pending.Kill true
        Expect.isTrue (pending.WaitForExit 5000) "The actual pending process was terminated"
        let attempt = own.Head.GetProperty("attemptId").GetString() |> nonNull

        let resolved =
            ProcessRunner.dotnet
                60000
                fixture.Cli
                [ "recovery"; "check"; attempt; "--format"; "json" ]
                fixture.Environment
                None

        let reply = (parse 0 resolved |> require "ACCEPTED").GetProperty("receipt")

        Expect.equal
            (reply.GetProperty("operationId").GetString())
            operation
            "Recovery resolves after abrupt process termination without resubmitting"

        Expect.isEmpty
            (listing fixture
             |> List.filter (fun record ->
                 record.GetProperty("operationId").GetString() = operation))
            "Only the selected resolved attempt is cleared"
    finally
        FixtureFiles.text fixture.Directory "redirect-release" "released" |> ignore

        if not pending.HasExited then
            pending.Kill true

        pending.WaitForExit(5000) |> ignore
        output.GetAwaiter().GetResult() |> ignore
        errors.GetAwaiter().GetResult() |> ignore
