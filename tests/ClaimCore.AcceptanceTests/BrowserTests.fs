module ClaimCore.AcceptanceTests.BrowserTests

open System
open System.Text
open System.Text.RegularExpressions
open System.Text.Json
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Expecto

let private selectEngines (value: string | null) =
    match value with
    | null -> [ "chromium"; "firefox"; "webkit" ]
    | "chromium" -> [ "chromium" ]
    | "firefox" -> [ "firefox" ]
    | "webkit" -> [ "webkit" ]
    | _ -> invalidOp "Acceptance browser engine must be chromium, firefox or webkit."

let private smokeScope (value: string | null) =
    match value with
    | null
    | "complete" -> false
    | "smoke" -> true
    | _ -> invalidOp "Acceptance browser scope must be complete or smoke."

let private engines =
    Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE"
    |> selectEngines

let private smoke =
    let selected =
        Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE"
        |> smokeScope

    if selected && engines.Length <> 1 then
        invalidOp
            "Smoke requires one explicit acceptance browser engine; it is not complete qualification."

    selected

let private executedCount minimum text =
    let plain = Regex.Replace(text, "\\x1B\\[[0-?]*[ -/]*[@-~]", "")
    let summary = Regex.Match(plain, @"(?m)^\s*([0-9]+) passed(?: \(([^\r\n]+)\))?\s*$")
    Expect.isTrue summary.Success "A successful browser run must report its executed tests"

    let count =
        Int32.Parse(summary.Groups[1].Value, Globalization.CultureInfo.InvariantCulture)

    Expect.isGreaterThanOrEqual
        count
        minimum
        "The declared browser scope must execute its whole inventory"

    Expect.isFalse
        (Regex.IsMatch(
            plain,
            @"\b[1-9][0-9]* (skipped|did not run|failed|interrupted|flaky|timed out)\b"
        ))
        "No incomplete browser qualification"

    count, summary.Groups[2].Value

let private report minimum engine (result: ClaimCore.TestSupport.BoundedProcessResult) =
    let count, duration =
        executedCount minimum (Encoding.UTF8.GetString result.StandardOutput)

    let scope =
        if smoke then
            "smoke; full browser qualification remains unrun"
        else
            "complete engine suite"

    let directory =
        match Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY" with
        | null ->
            Path.Combine(
                ClaimCore.TestSupport.RepositoryRoot.find (),
                "artifacts",
                "verification",
                "native." + Guid.NewGuid().ToString("N")
            )
        | value -> value

    Directory.CreateDirectory directory |> ignore

    JsonSerializer.SerializeToUtf8Bytes
        {|
            runId = Path.GetFileName directory
            engine = engine
            scope = if smoke then "smoke" else "complete"
            passed = count
            duration = duration
        |}
    |> FixtureFiles.write directory ("browser-" + engine + ".json")
    |> ignore

    Console.WriteLine($"Published {engine} ({scope}): {count} passed; {duration}")

let private completionControls () =
    for minimum, underFloor in [ 11, "10 passed"; 131, "130 passed" ] do
        let complete = $"{minimum} passed (4.7m)"

        for incomplete in
            [ ""; "0 passed"; underFloor ]
            @ ([ "skipped"; "did not run"; "failed"; "flaky"; "interrupted"; "timed out" ]
               |> List.map (fun reason -> $"{complete}\n1 {reason}")) do
            Expect.throws
                (fun () -> executedCount minimum incomplete |> ignore)
                "Smoke and complete reports refuse under-floor or incomplete execution"

        for report in [ complete; $"\u001b[32m{complete}\u001b[0m" ] do
            Expect.equal
                (executedCount minimum report)
                (minimum, "4.7m")
                "Each declared scope admits complete evidence, including terminal colour"

let private selectionControls () =
    Expect.equal
        (selectEngines null)
        [ "chromium"; "firefox"; "webkit" ]
        "Default keeps every engine"

    for engine in [ "chromium"; "firefox"; "webkit" ] do
        Expect.equal (selectEngines engine) [ engine ] "Explicit isolated engine job"

    for value in [ ""; "all"; "unknown" ] do
        Expect.throws
            (fun () -> selectEngines value |> ignore)
            "Invalid scope cannot silently omit engines"

        Expect.throws
            (fun () -> smokeScope value |> ignore)
            "Invalid campaign cannot silently shorten work"

    Expect.isFalse (smokeScope null) "Default remains complete"
    Expect.isFalse (smokeScope "complete") "Complete scope is explicit"
    Expect.isTrue (smokeScope "smoke") "Smoke is explicitly selected"

let private runSelected (qualify: string -> Task<unit>) selected =
    task {
        for engine in selected do
            do! qualify engine
    }

let private orchestrationControls () =
    let selected = [ "chromium"; "firefox"; "webkit" ]
    let observed = ResizeArray<string>()

    let complete engine =
        observed.Add engine
        Task.FromResult()

    (runSelected complete selected).GetAwaiter().GetResult()
    Expect.equal (Seq.toList observed) selected "Success executes every selected engine in order"

    for refused, expected in [ "chromium", [ "chromium" ]; "firefox", [ "chromium"; "firefox" ] ] do
        observed.Clear()

        let qualify engine =
            observed.Add engine

            if engine = refused then
                Task.FromException<unit>(InvalidOperationException "Synthetic engine refusal.")
            else
                Task.FromResult()

        Expect.throws
            (fun () -> (runSelected qualify selected).GetAwaiter().GetResult())
            "An engine failure remains a failing browser qualification"

        Expect.equal (Seq.toList observed) expected "Failure never starts later expensive fixtures"

let private browserArguments engine =
    let specifications =
        if smoke then
            [
                "authority-uncertainty.spec.ts"
                "command-lifecycle.spec.ts"
                "localization-correction.spec.ts"
                "stewardship-localization.spec.ts"
                "unicode-parity.spec.ts"
            ]
        else
            []

    [ "exec"; "--"; "playwright"; "test" ]
    @ specifications
    @ [ "--project"; engine; "--forbid-only"; "--max-failures=1" ]
    @ (if smoke then [ "--global-timeout=120000" ] else [])

let private printFailureSummary root engine bytes =
    let codeFiles =
        try
            Directory.EnumerateFiles(
                Path.Combine(root, "web", "e2e"),
                "*",
                SearchOption.AllDirectories
            )
            |> Seq.toArray
        with
        | :? IOException
        | :? UnauthorizedAccessException -> [||]

    let passed, failed = BrowserProgress.counts bytes
    let locations = BrowserProgress.failedLocations codeFiles bytes |> String.concat ","
    let durations = BrowserProgress.failedDurations codeFiles bytes |> String.concat ","
    let stack = BrowserProgress.stackLocations codeFiles bytes |> String.concat ","
    let categories = BrowserProgress.failureCategories bytes |> String.concat ","

    Console.WriteLine(
        $"Published {engine}: completed rows passed={passed}, failed={failed}; categories={categories}; locations={locations}; durations={durations}; stack={stack}"
    )

let private runBrowser root engine output start =
    try
        PublishedProcesses.run 900_000 start None
    with :? ClaimCore.TestSupport.BoundedProcessFailure as failure ->
        FixtureFiles.write output "playwright.stdout" failure.StandardOutput |> ignore
        FixtureFiles.write output "playwright.stderr" failure.StandardError |> ignore
        printFailureSummary root engine failure.StandardOutput

        Console.WriteLine(
            $"Published {engine}: bounded process cleanupFailed={failure.CleanupFailed}"
        )

        failtest ($"Browser process failure: {failure.Kind}; bounded private evidence retained.")

let private qualifyEngine engine =
    task {
        let scope =
            if smoke then
                "smoke; complete qualification is not being run"
            else
                "complete engine suite"

        Console.WriteLine(
            $"Starting published {engine} {scope}; isolated fixture, one worker, zero retries."
        )

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

        let start = PublishedProcesses.start "npm" (browserArguments engine) values

        start.WorkingDirectory <- Path.Combine(fixture.Root, "web")
        let result = runBrowser fixture.Root engine output start
        FixtureFiles.write output "playwright.stdout" result.StandardOutput |> ignore
        FixtureFiles.write output "playwright.stderr" result.StandardError |> ignore

        if result.ExitCode <> 0 then
            printFailureSummary fixture.Root engine result.StandardOutput

        Expect.equal
            result.ExitCode
            0
            ("Published browser scope " + engine + "; private evidence retained")

        report (if smoke then 11 else 131) engine result
        BrowserReadback.qualify fixture output
        fixture.Complete()
    }

[<Tests>]
let tests =
    testSequenced
    <| testList
        (if smoke then
             "published browser smoke"
         else
             "published browser qualification")
        [
            testCase
                "completion evidence rejects missing, under-floor and skipped reports"
                (fun () ->
                    completionControls ()
                    selectionControls ()
                    orchestrationControls ())
            testCaseAsync
                ((if smoke then
                      "workflow, localization and Unicode smoke on "
                  else
                      "complete browser specification suites on ")
                 + String.concat ", " engines)
                (async { do! runSelected qualifyEngine engines |> Async.AwaitTask })
        ]
