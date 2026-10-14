module ClaimCore.AcceptanceTests.BrowserTests

open System
open System.Text
open System.Text.RegularExpressions
open System.Collections.Generic
open System.IO
open Expecto

let private executedCount text =
    let plain = Regex.Replace(text, "\\x1B\\[[0-?]*[ -/]*[@-~]", "")
    let summary = Regex.Match(plain, @"(?m)^\s*([0-9]+) passed(?: \(([^\r\n]+)\))?\s*$")
    Expect.isTrue summary.Success "A successful browser run must report its executed tests"

    let count =
        Int32.Parse(summary.Groups[1].Value, Globalization.CultureInfo.InvariantCulture)

    Expect.isGreaterThanOrEqual count 229 "Complete browser floor; never lower to clear a run"

    Expect.isFalse
        (Regex.IsMatch(plain, @"\b[1-9][0-9]* (skipped|did not run|failed|interrupted)\b"))
        "No incomplete browser qualification"

    count, summary.Groups[2].Value

let private report engine (result: ClaimCore.TestSupport.BoundedProcessResult) =
    let count, duration = executedCount (Encoding.UTF8.GetString result.StandardOutput)
    Console.WriteLine($"Published {engine}: {count} passed; {duration}")

let private completionControls () =
    for incomplete in
        [
            ""
            "0 passed"
            "228 passed"
            "229 passed\n1 skipped"
            "229 passed\n1 did not run"
        ] do
        Expect.throws (fun () -> executedCount incomplete |> ignore) "Incomplete report must refuse"

    for complete in [ "229 passed (4.7m)"; "\u001b[32m229 passed (4.7m)\u001b[0m" ] do
        Expect.equal
            (executedCount complete)
            (229, "4.7m")
            "Complete report admitted, including terminal colour"

let private qualifyEngine engine =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        CliCalls.setup fixture

        let output = Path.Combine(fixture.Directory, engine)
        Directory.CreateDirectory output |> ignore
        let values = Dictionary<string, string>()
        values["CLAIMCORE_WEB_BASE_URL"] <- fixture.Origin
        values["CLAIMCORE_WEB_E2E_BACKEND_URL"] <- fixture.Backend
        values["CLAIMCORE_WEB_E2E_ENGINE"] <- engine
        values["CLAIMCORE_WEB_E2E_REDIRECT_STATE_DIR"] <- fixture.Directory
        values["CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR"] <- output

        values["CLAIMCORE_TEST_OIDC_CREDENTIALS"] <-
            Path.Combine(fixture.Directory, "credentials.json")

        values["CLAIMCORE_WEB_E2E_PRINCIPALS_FILE"] <-
            Path.Combine(fixture.Directory, "principals.json")

        let start =
            PublishedProcesses.start
                "npm"
                [
                    "exec"
                    "--"
                    "playwright"
                    "test"
                    "--project"
                    engine
                    "--forbid-only"
                    "--max-failures=1"
                ]
                values

        start.WorkingDirectory <- Path.Combine(fixture.Root, "web")
        let result = PublishedProcesses.run 900_000 start None
        FixtureFiles.write output "playwright.stdout" result.StandardOutput |> ignore
        FixtureFiles.write output "playwright.stderr" result.StandardError |> ignore

        Expect.equal
            result.ExitCode
            0
            ("Complete published browser suite " + engine + "; private logs: " + output)

        report engine result
        BrowserReadback.qualify fixture output
        fixture.Complete()
    }

[<Tests>]
let tests =
    testList
        "published browser qualification"
        ([
            testCase
                "completion evidence rejects missing, under-floor and skipped reports"
                completionControls
         ]
         @ ([ "chromium"; "firefox"; "webkit" ]
            |> List.map (fun engine ->
                testCaseAsync
                    ("case work, admission, sessions, localization and accessibility on " + engine)
                    (async { do! qualifyEngine engine |> Async.AwaitTask }))))
