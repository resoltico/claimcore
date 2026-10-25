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
open ClaimCore.Contract
open ClaimCore.Contract.Protocol
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
            destination
            environment
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

let private fallback (operation: string) (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode 4 "Undeliverable mutation confirmation remains uncertain"
    let lines = Encoding.UTF8.GetString(result.StandardError).TrimEnd('\n').Split('\n')
    Expect.equal lines.Length 3 "Fallback reply, guidance, and exact request frame"
    Expect.stringContains lines[1] "claimcore call" "Exact resending guidance"
    Expect.stringContains lines[1] "claimcore operation resolve" "Final resolution guidance"

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
