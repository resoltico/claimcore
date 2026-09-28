module ClaimCore.DocsTests.FrontendReportInventoryTests

open System.Text.Json
open System.Text
open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

let private rows (names: Set<string>) =
    names
    |> Set.toArray
    |> Array.map (fun id ->
        {|
            id = id
            outcome = "passed"
            durationMs = 0
        |})

let private vitestBytes passed names =
    JsonSerializer.SerializeToUtf8Bytes(
        {|
            format = "claimcore-vitest-report"
            formatVersion = 1
            status = "passed"
            totals =
                {|
                    passed = passed
                    failed = 0
                    skipped = 0
                    todo = 0
                |}
            tests = rows names
        |}
    )

let private browserBytes engine expected passed names =
    JsonSerializer.SerializeToUtf8Bytes(
        {|
            format = "claimcore-playwright-report"
            formatVersion = 1
            scope = engine
            status = "passed"
            expected = expected
            totals =
                {|
                    passed = passed
                    failed = 0
                    skipped = 0
                    timedOut = 0
                    interrupted = 0
                |}
            tests = rows names
            failureLines = [||]
            failureCodes = [||]
        |}
    )

let private vitestInventory =
    testCase "Vitest report requires the exact current catalog count and identities" (fun () ->
        let names = FrontendTestCatalog.vitest
        let actual = names.Count
        Expect.isOk (StructuredReports.validateVitestBytes (vitestBytes actual names)) "Exact"

        Expect.isError
            (StructuredReports.validateVitestBytes (vitestBytes (actual - 1) names))
            "A stale hardcoded count cannot pass"

        Expect.isError
            (StructuredReports.validateVitestBytes (
                vitestBytes actual (names |> Set.remove (Set.minElement names))
            ))
            "A missing live identity cannot pass"

        let report = vitestBytes actual names |> Encoding.UTF8.GetString
        let fractional = report.Replace("\"durationMs\":0", "\"durationMs\":0.5")
        Expect.notEqual fractional report "The malformed-duration fixture must change the report"

        Expect.equal
            (StructuredReports.validateVitestBytes (Encoding.UTF8.GetBytes fractional))
            (Error "Structured report counter 'durationMs' is invalid.")
            "Fractional duration reports must fail with their actual reason"

        use repository = new TempRepository()

        repository.WriteBytes("artifacts/frontend/vitest-summary.json", vitestBytes actual names)
        |> ignore

        Expect.isOk
            (LocalFrontendReports.verifyVitest repository.Root)
            "The local gate validates a real report file against the compiled catalog"

        repository.WriteBytes(
            "artifacts/frontend/vitest-summary.json",
            vitestBytes (actual - 1) names
        )
        |> ignore

        Expect.isError
            (LocalFrontendReports.verifyVitest repository.Root)
            "The local gate rejects a stale producer count"

        let changedNames =
            names |> Set.remove (Set.minElement names) |> Set.add "unregistered identity"

        repository.WriteBytes(
            "artifacts/frontend/vitest-summary.json",
            vitestBytes actual changedNames
        )
        |> ignore

        Expect.isError
            (LocalFrontendReports.verifyVitest repository.Root)
            "The local gate rejects a changed identity even when the count is unchanged")

let private browserInventory =
    testCase "Playwright report requires the exact current catalog count and identities" (fun () ->
        let names = FrontendTestCatalog.browser
        let actual = names.Count

        Expect.isOk
            (StructuredReports.validateBrowserBytes
                "chromium"
                (browserBytes "chromium" actual actual names))
            "Exact browser report"

        Expect.isError
            (StructuredReports.validateBrowserBytes
                "chromium"
                (browserBytes "chromium" (actual + 1) actual names))
            "A stale browser expected count cannot pass"

        Expect.isError
            (StructuredReports.validateBrowserBytes
                "firefox"
                (browserBytes "chromium" actual actual names))
            "A different engine report cannot be reused"

        use repository = new TempRepository()

        for engine in [ "chromium"; "firefox"; "webkit" ] do
            repository.WriteBytes(
                $"artifacts/browser/{engine}.json",
                browserBytes engine actual actual names
            )
            |> ignore

            Expect.isOk
                (LocalFrontendReports.verifyBrowser repository.Root engine)
                "Each local browser report matches the exact catalog"

        Expect.isError
            (LocalFrontendReports.verifyBrowser repository.Root "other")
            "An unregistered browser engine is refused"

        repository.WriteBytes(
            "artifacts/frontend/vitest-summary.json",
            vitestBytes FrontendTestCatalog.vitest.Count FrontendTestCatalog.vitest
        )
        |> ignore

        Expect.isOk (LocalFrontendReports.verifyAll repository.Root) "All local reports agree"

        repository.WriteBytes(
            "artifacts/browser/firefox.json",
            browserBytes "firefox" actual (actual - 1) names
        )
        |> ignore

        Expect.isError
            (LocalFrontendReports.verifyAll repository.Root)
            "A stale single-engine report fails the combined local gate")

let tests =
    testList "Frontend report inventory" [ vitestInventory; browserInventory ]
