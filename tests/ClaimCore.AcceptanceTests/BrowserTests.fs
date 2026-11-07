module ClaimCore.AcceptanceTests.BrowserTests

open System
open System.Collections.Generic
open System.IO
open Expecto

let private engines =
    match Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE" with
    | null -> [ "chromium"; "firefox"; "webkit" ]
    | "chromium" -> [ "chromium" ]
    | "firefox" -> [ "firefox" ]
    | "webkit" -> [ "webkit" ]
    | _ -> invalidOp "Browser engine must be chromium, firefox or webkit."

let private specification =
    Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_SPEC"
    |> Option.ofObj

let private run engine =
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

        let arguments =
            [ "exec"; "--"; "playwright"; "test" ]
            @ (specification |> Option.toList)
            @ [ "--project"; engine ]
            @ (if engine = "chromium" then [] else [ "--grep"; "@engine" ])

        let start = PublishedProcesses.start "npm" arguments values
        start.WorkingDirectory <- Path.Combine(fixture.Root, "web")

        let result =
            try
                PublishedProcesses.run (21 * 60_000) start None
            with :? ClaimCore.TestSupport.BoundedProcessFailure as failure ->
                FixtureFiles.write output "playwright.stdout" failure.StandardOutput |> ignore
                FixtureFiles.write output "playwright.stderr" failure.StandardError |> ignore
                reraise ()

        FixtureFiles.write output "playwright.stdout" result.StandardOutput |> ignore
        FixtureFiles.write output "playwright.stderr" result.StandardError |> ignore
        Expect.equal result.ExitCode 0 ("Playwright " + engine + "; private evidence: " + output)

        if
            engine = "chromium"
            && (specification |> Option.forall (fun value -> value.Contains("unicode-parity")))
        then
            BrowserReadback.qualify fixture output

        fixture.Complete()
    }

[<Tests>]
let tests =
    testCaseAsync
        "published browser acceptance"
        (async {
            for engine in engines do
                do! run engine |> Async.AwaitTask
        })
