module internal ClaimCore.AcceptanceTests.CliNativeOutput

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Wire
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

    let start =
        ProcessStartInfo(
            "python3",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    for argument in
        [
            Path.Combine(fixture.Root, "tests/fixtures/output/native_output.py")
            "run"
            fixture.Cli
            command
            destination
            directory
        ] do
        start.ArgumentList.Add argument

    ClaimCore.TestSupport.CliProcessEnvironment.clearInherited start

    for pair: KeyValuePair<string, string> in environment do
        start.Environment[pair.Key] <- pair.Value

    let result = ClaimCore.TestSupport.BoundedProcess.run start (Some source) 8192 60000
    Expect.equal result.ExitCode 0 "Native descriptor driver completes"
    use status = JsonDocument.Parse result.StandardOutput

    {
        ExitCode = status.RootElement.GetProperty("exitCode").GetInt32()
        StandardOutput = File.ReadAllBytes(Path.Combine(directory, "stdout"))
        StandardError = File.ReadAllBytes(Path.Combine(directory, "stderr"))
    }

let private authored (fixture: PublishedFixture.Fixture) =
    let request =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = request["input"] |> nonNull
    let operation = id ()
    let reference = "NATIVE-OUTPUT-" + id ()
    input["operationId"] <- JsonValue.Create operation
    input["caseReference"] <- JsonValue.Create reference
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
        automation fixture "case.get" {| caseReference = reference |} 0
        |> require "SUCCEEDED"

    Expect.equal (current.GetProperty("revision").GetInt64()) 1L "One accepted case revision"

    let history =
        automation
            fixture
            "case.history"
            {|
                caseReference = reference
                detail = "FULL"
                limit = 50
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal
        (history.GetProperty("entries").GetArrayLength())
        1
        "Exactly one accepted operation"

let private fallback operation (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode 4 "Undeliverable mutation confirmation remains uncertain"
    use document = JsonDocument.Parse result.StandardError

    let frame =
        Frames.decode result.StandardError
        |> Result.defaultWith (fun _ -> failtest "Fallback must use the existing Wire codec")

    Expect.equal
        (Frames.metadata frame).AttemptKnowledge
        ClaimCore.Catalog.AttemptKnowledge.Unconfirmed
        "No false confirmation"

    Expect.equal
        (document.RootElement.GetProperty("operationId").GetString())
        operation
        "Original caller identity retained"

    Expect.equal
        (document.RootElement.GetProperty("resolutionEvidence").GetString())
        "NONE"
        "No fabricated acceptance evidence"

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
                automation fixture "case.get" {| caseReference = laterReference |} 2
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
                (document.RootElement.GetProperty("attemptKnowledge").GetString())
                "READ_FAILED"
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
    let request = frame "case.get" {| caseReference = reference |}

    let result =
        run
            fixture
            fixture.Environment
            "session"
            "pipe"
            (Array.concat [ request; [| 10uy |]; request; [| 10uy |] ])

    Expect.equal result.ExitCode 0 "Normal multiframe session succeeds"

    let rows =
        Encoding.UTF8
            .GetString(result.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)

    Expect.equal rows.Length 2 "One complete newline-delimited frame per read"
    Expect.isTrue (rows[0] = rows[1]) "Repeated Unicode frames never overwrite one another"

    Expect.equal
        result.StandardOutput[result.StandardOutput.Length - 1]
        10uy
        "Final newline is retained"

    for row in rows do
        Expect.isOk
            (Frames.decode (Encoding.UTF8.GetBytes row))
            "Unicode machine bytes remain admitted"

let qualify fixture =
    mutations fixture
    noMutation fixture
    unicodeFrames fixture
