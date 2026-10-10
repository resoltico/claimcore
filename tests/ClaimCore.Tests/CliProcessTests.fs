module ClaimCore.Tests.CliProcessTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text
open Expecto
open ClaimCore.Application
open ClaimCore.TestSupport

type ProcessResult =
    {
        ExitCode: int
        StandardOutput: string
        StandardError: string
    }

let private root = RepositoryRoot.find ()

let runDotnet arguments input =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.WorkingDirectory <- root
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardInput <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    CliProcessEnvironment.clearInherited startInfo
    startInfo.Environment["DOTNET_NOLOGO"] <- "1"
    arguments |> List.iter startInfo.ArgumentList.Add
    let bytes = Encoding.UTF8.GetBytes(input: string)
    let result = BoundedProcess.run startInfo (Some bytes) (16 * 1024 * 1024) 120_000

    {
        ExitCode = result.ExitCode
        StandardOutput = UTF8Encoding(false, true).GetString(result.StandardOutput)
        StandardError = UTF8Encoding(false, true).GetString(result.StandardError)
    }

let private resolveCliPath () =
    let project = Path.Combine(root, "src/ClaimCore.Cli/ClaimCore.Cli.fsproj")

    let result =
        runDotnet
            [
                "msbuild"
                project
                "-nologo"
                "-verbosity:quiet"
                "-property:Configuration=Release"
                "-getProperty:TargetPath"
            ]
            ""

    let target = result.StandardOutput.Trim()

    if
        result.ExitCode <> 0
        || not (String.IsNullOrEmpty(result.StandardError))
        || not (File.Exists(target))
    then
        failwith "Could not resolve the built CLI TargetPath."

    target

let private cliPath = lazy (resolveCliPath ())

let private invoke arguments input =
    runDotnet (cliPath.Value :: arguments) input

let private json text = JsonDocument.Parse(text: string)

let private expectJson output assertion =
    use document = json output
    Expect.equal document.RootElement.ValueKind JsonValueKind.Object "One JSON object"
    assertion document.RootElement

let private discoveryArray arguments expectedCount =
    let result = invoke arguments ""
    Expect.equal result.ExitCode 0 "Configuration-free discovery exit"
    Expect.equal result.StandardError "" "No discovery stderr"
    use document = json result.StandardOutput
    Expect.equal document.RootElement.ValueKind JsonValueKind.Array "Generated discovery array"
    Expect.equal (document.RootElement.GetArrayLength()) expectedCount "Exact catalog length"

let private discoverySchema arguments =
    let result = invoke arguments ""
    Expect.equal result.ExitCode 0 "Configuration-free schema exit"
    Expect.equal result.StandardError "" "No schema stderr"

    expectJson result.StandardOutput (fun root ->
        Expect.equal
            (root.GetProperty("$schema").GetString())
            "https://json-schema.org/draft/2020-12/schema"
            "Generated JSON Schema draft")

let private allDiscoveryVariants =
    testCase "all structured discovery variants use the generated contract" (fun () ->
        let version = invoke [ "version"; "--json" ] ""
        Expect.equal version.ExitCode 0 "JSON version exit"

        expectJson version.StandardOutput (fun root ->
            Expect.equal
                (root.GetProperty("version").GetString())
                BuildIdentity.current.Version
                "Compiled product identity")

        discoveryArray [ "describe"; "fields" ] 13
        discoveryArray [ "describe"; "commands" ] 9
        discoveryArray [ "describe"; "endpoints" ] 30

        for arguments in
            [
                [ "schema"; "invocation" ]
                [ "schema"; "response" ]
                [ "schema"; "definition" ]
                [ "schema"; "recovery-envelope" ]
                [ "schema"; "endpoint"; "case.get" ]
            ] do
            discoverySchema arguments)

let private discoveryTests =
    testList
        "CLI v4 discovery processes"
        [
            testCase "help and plain version are configuration-free human text" (fun () ->
                let help = invoke [ "help" ] ""
                let version = invoke [ "version" ] ""
                Expect.equal help.ExitCode 0 "Help exit"
                Expect.stringContains help.StandardOutput "ClaimCore CLI v4" "Current grammar"
                Expect.equal version.ExitCode 0 "Version exit"

                Expect.equal
                    (version.StandardOutput.Trim())
                    BuildIdentity.current.Version
                    "Compiled version")
            testCase "structured discovery is configuration-free and contract-backed" (fun () ->
                let summary = invoke [ "describe"; "summary" ] ""
                let schema = invoke [ "schema"; "invocation" ] ""
                Expect.equal summary.ExitCode 0 "Summary exit"
                Expect.equal schema.ExitCode 0 "Schema exit"

                expectJson summary.StandardOutput (fun root ->
                    Expect.equal
                        (root.GetProperty("cliProtocolVersion").GetInt32())
                        4
                        "CLI version"

                    Expect.equal
                        (root.GetProperty("businessFieldCount").GetInt32())
                        13
                        "Field count")

                expectJson schema.StandardOutput (fun root ->
                    Expect.equal
                        (root.GetProperty("$schema").GetString())
                        "https://json-schema.org/draft/2020-12/schema"
                        "Generated schema draft"))
            allDiscoveryVariants
        ]

let private callFrame =
    """{"protocolVersion":4,"endpoint":"case.list","input":{"limit":1}}"""

let private callTests =
    testList
        "CLI v4 call"
        [
            testCase "call reports strict frame errors as one protocol JSON response" (fun () ->
                let result = invoke [ "call" ] "{}"
                Expect.equal result.ExitCode 2 "Protocol exit"
                Expect.equal result.StandardError "" "No sensitive stderr"

                expectJson result.StandardOutput (fun root ->
                    Expect.equal
                        (root.GetProperty("kind").GetString())
                        "protocolFailure"
                        "Tagged failure"

                    Expect.equal
                        (root.GetProperty("protocolVersion").GetInt32())
                        4
                        "Frame version"))
            testCase "call refuses case work without authenticated service configuration" (fun () ->
                let result = invoke [ "call" ] callFrame
                Expect.equal result.ExitCode 3 "Runtime configuration exit"

                expectJson result.StandardOutput (fun root ->
                    Expect.equal
                        (root.GetProperty("kind").GetString())
                        "localFailure"
                        "Tagged configuration failure"

                    Expect.equal
                        (root.GetProperty("code").GetString())
                        "CLI_CONFIGURATION_INVALID"
                        "Safe code"))
        ]

let private oversizedOpenSession () =
    let start = ProcessStartInfo("dotnet")
    start.UseShellExecute <- false
    start.RedirectStandardInput <- true
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    CliProcessEnvironment.clearInherited start
    start.ArgumentList.Add cliPath.Value
    start.ArgumentList.Add "session"

    use child =
        Process.Start start
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "CLI process did not start")

    try
        let lifetime = TimeSpan.FromSeconds 20.

        let output =
            BoundedProcess.readText (16 * 1024 * 1024) child.StandardOutput lifetime

        let errors = BoundedProcess.readText (16 * 1024 * 1024) child.StandardError lifetime

        let bytes = Array.create 131073 (byte 'x')
        child.StandardInput.BaseStream.Write(bytes, 0, bytes.Length)
        child.StandardInput.BaseStream.Flush()
        Expect.isTrue (child.WaitForExit 20000) "Session refuses without newline or stdin EOF"
        Expect.equal child.ExitCode 2 "Terminal protocol refusal"
        Expect.equal (errors.GetAwaiter().GetResult()) "" "No process failure"

        expectJson (output.GetAwaiter().GetResult()) (fun root ->
            Expect.equal (root.GetProperty("code").GetString()) "FRAME_TOO_LARGE" "Exact refusal")
    finally
        if not child.HasExited then
            child.Kill(true)
            child.WaitForExit(2000) |> ignore

let private isolatedEnvironment () =
    let start = ProcessStartInfo("dotnet")

    for name in [ "CLAIMCORE_SERVICE_URL"; "claimcore_oidc_issuer"; "COVERLET_TEST_FILE" ] do
        start.Environment[name] <- "synthetic-ambient-value"

    start.Environment["CLAIMCOREX_FIXTURE_MARKER"] <- "preserved"
    CliProcessEnvironment.clearInherited start

    for name in [ "CLAIMCORE_SERVICE_URL"; "claimcore_oidc_issuer"; "COVERLET_TEST_FILE" ] do
        Expect.isFalse (start.Environment.ContainsKey name) "Ambient configuration is excluded"

    Expect.equal
        start.Environment["CLAIMCOREX_FIXTURE_MARKER"]
        "preserved"
        "Other environment remains"

let private sessionTests =
    testList
        "CLI v4 session and hard break"
        [
            testCase
                "CLI fixture children exclude inherited credentials and coverage"
                isolatedEnvironment
            testCase
                "[CC-CLI-003] oversized session refuses while its input remains open"
                oversizedOpenSession
            testCase
                "session emits a local protocol frame for a blank line and exits cleanly"
                (fun () ->
                    let result = invoke [ "session" ] "\n"
                    Expect.equal result.ExitCode 0 "Clean EOF session exit"
                    Expect.equal result.StandardError "" "No stderr"

                    expectJson result.StandardOutput (fun root ->
                        Expect.equal
                            (root.GetProperty("kind").GetString())
                            "protocolFailure"
                            "Frame-local failure"

                        Expect.equal
                            (root.GetProperty("code").GetString())
                            "BLANK_FRAME"
                            "Exact frame code"))
            testCase "removed protocol-v2 verbs are unsupported process invocations" (fun () ->
                let result = invoke [ "capabilities" ] ""
                Expect.equal result.ExitCode 64 "Hard process grammar break"
                use error = JsonDocument.Parse(result.StandardError)

                Expect.equal
                    (error.RootElement.GetProperty("diagnostic").GetProperty("id").GetString())
                    "CLI_INVOCATION_UNSUPPORTED"
                    "Typed safe usage direction")
        ]

let private invocationTests =
    testList "CLI v4 call and session" [ callTests; sessionTests ]

let tests =
    testList "CLI v4 child-process contract" [ discoveryTests; invocationTests ]
