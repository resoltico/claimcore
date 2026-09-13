module ClaimCore.DocsTests.FrontendReportInventoryTests

open System.Text.Json
open Expecto
open ClaimCore.Docs

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
            "A missing live identity cannot pass")

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
            "A different engine report cannot be reused")

let tests =
    testList "Frontend report inventory" [ vitestInventory; browserInventory ]
