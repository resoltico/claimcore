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
        ProcessRunner.dotnet 60000 fixture.Cli [ "recovery"; "list" ] fixture.Environment None

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
    let operation, reference = id (), "RECOVERY-PROCESS-" + id ()
    input["operationId"] <- JsonValue.Create operation
    (input["target"] |> nonNull)["caseReference"] <- JsonValue.Create reference
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

/// Two actual processes share an operation UUID. Only the still-pending attempt survives a replay.
let qualify (fixture: PublishedFixture.Fixture) =
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
                    "subject"
                    "act"
                ]

        Expect.equal
            (own.Head.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
            expectedKeys
            "Only identity fields are retained"

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
                [ "recovery"; "resolve"; attempt ]
                fixture.Environment
                None

        let reply = parse 0 resolved |> require "ACCEPTED"

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
