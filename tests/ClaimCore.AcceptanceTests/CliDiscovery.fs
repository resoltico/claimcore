module internal ClaimCore.AcceptanceTests.CliDiscovery

open ClaimCore.Cli

open System
open System.IO
open System.Collections.Generic
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

let private configurationRefusals (fixture: PublishedFixture.Fixture) =
    let source = Some(Encoding.UTF8.GetBytes "{\"endpoint\":\"context\",\"input\":{}}")

    for environment, setting, issue in
        [
            let invalid = Dictionary<string, string>()
            invalid["CLAIMCORE_SERVICE_URL"] <- "secret-canary\u001bhttps://private.example/"
            yield invalid, "CLAIMCORE_SERVICE_URL", "INVALID"
            let missing = Dictionary<string, string>()
            missing["CLAIMCORE_SERVICE_URL"] <- "https://localhost/"
            yield missing, "CLAIMCORE_OIDC_ISSUER", "MISSING"
            let privateFile = Dictionary<string, string>(fixture.Environment)

            privateFile["CLAIMCORE_OIDC_CLIENT_SECRET_FILE"] <-
                System.IO.Path.Combine(fixture.Directory, "secret-canary-missing")

            yield privateFile, "CLAIMCORE_OIDC_CLIENT_SECRET_FILE", "PRIVATE_INPUT_REFUSED"
        ] do
        let result = ProcessRunner.dotnet 8_000 fixture.Cli [ "call" ] environment source
        Expect.equal result.ExitCode 3 "Known configuration refusal"
        Expect.equal result.StandardError.Length 0 "One machine frame without prose"

        Expect.isFalse
            (Encoding.UTF8.GetString(result.StandardOutput).Contains "secret-canary")
            "No private value or path echo"

        use document = JsonDocument.Parse result.StandardOutput

        let parameters =
            document.RootElement.GetProperty("result").GetProperty("localFailure")

        Expect.equal
            (parameters.GetProperty("setting").GetString())
            setting
            "Known setting survives"

        Expect.equal (parameters.GetProperty("issue").GetString()) issue "Known cause survives"

let private configurationGuidance (fixture: PublishedFixture.Fixture) =
    let environment = Dictionary<string, string>()
    environment["CLAIMCORE_SERVICE_URL"] <- "https://localhost/path?token=SECRET-CANARY"

    let result =
        ProcessRunner.dotnet
            8000
            fixture.Cli
            [ "call" ]
            environment
            (Some(Encoding.UTF8.GetBytes "{\"endpoint\":\"context\",\"input\":{}}"))

    Expect.equal result.ExitCode 3 "Invalid service form remains a safe definite local failure"
    Expect.equal result.StandardError.Length 0 "One machine stdout frame"
    use frame = JsonDocument.Parse result.StandardOutput

    Expect.equal
        (frame.RootElement.GetProperty("delivery").GetString())
        "NOT_SENT"
        "This attempt was not sent"

    Expect.isFalse
        (Encoding.UTF8.GetString(result.StandardOutput).Contains "SECRET-CANARY")
        "No configuration value echo"

    let id = InputDiagnostics.configurationId

    let invoke args =
        ProcessRunner.dotnet 8000 fixture.Cli args (Dictionary<string, string>()) None

    // Every language's alias equality is covered by ConfigurationHelpTests.helpAlias.
    for language in [ "en" ] do
        let alias = invoke [ "help"; id; "--language"; language ]
        let topic = invoke [ "help"; "configuration"; "--language"; language ]
        Expect.equal alias.ExitCode 0 "Actual diagnostic ID resolves offline"

        Expect.isTrue
            (alias.StandardOutput = topic.StandardOutput)
            "Both entry points render identical core content"

let private missingConfiguration
    (invoke: string list -> byte array option -> ProcessRunner.Result)
    =
    let configured =
        invoke [ "call" ] (Some(Encoding.UTF8.GetBytes "{\"endpoint\":\"context\",\"input\":{}}"))

    Expect.equal configured.ExitCode 3 "Missing configuration is definite"
    use document = JsonDocument.Parse configured.StandardOutput

    let parameters =
        document.RootElement.GetProperty("result").GetProperty("localFailure")

    Expect.equal
        (parameters.GetProperty("setting").GetString())
        "CLAIMCORE_SERVICE_URL"
        "First known missing setting"

    Expect.equal
        (parameters.GetProperty("issue").GetString())
        "MISSING"
        "Missing is distinct from invalid"

    Expect.equal configured.StandardError.Length 0 "Machine failures remain one frame"

let qualify (fixture: PublishedFixture.Fixture) =
    configurationRefusals fixture
    configurationGuidance fixture
    let environment = Dictionary<string, string>()

    let invoke args input =
        ProcessRunner.dotnet 8_000 fixture.Cli args environment input

    missingConfiguration invoke
