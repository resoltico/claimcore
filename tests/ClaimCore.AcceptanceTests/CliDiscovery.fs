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

let private schemaFailureLanguage (fixture: PublishedFixture.Fixture) =
    let create language =
        let root = Path.Combine(fixture.Directory, "language-profile-" + language)
        let directory = Path.Combine(root, "claimcore")

        Directory.CreateDirectory(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

        FixtureFiles.text directory "profile.json" ($"{{\"language\":\"{language}\"}}")
        |> ignore

        root

    let profileLv = create "lv"
    let profileAr = create "ar"
    let empty = Path.Combine(fixture.Directory, "language-profile-empty")

    let environment root values =
        let result = Dictionary<string, string>()

        for name, value in
            [
                "XDG_CONFIG_HOME", root
                "LC_ALL", ""
                "LC_MESSAGES", ""
                "LANG", "en_US.UTF-8"
            ]
            @ values do
            result[name] <- value

        result

    let expected =
        ProcessRunner.dotnet
            8_000
            fixture.Cli
            [ "schema"; "invocation" ]
            (environment empty [])
            None

    Expect.equal expected.ExitCode 0 "Known machine schema succeeds without connection settings"

    for source, selected in
        [
            "environment", environment profileAr [ "CLAIMCORE_LANGUAGE", "lv" ]
            "profile", environment profileLv [ "LC_ALL", "ar_EG.UTF-8" ]
            "LC_ALL", environment empty [ "LC_ALL", "lv_LV.UTF-8" ]
            "LC_MESSAGES", environment empty [ "LC_MESSAGES", "lv_LV.UTF-8"; "LANG", "ar_EG.UTF-8" ]
        ] do
        let failed =
            ProcessRunner.dotnet 8_000 fixture.Cli [ "schema"; "unknown-l7-native" ] selected None

        Expect.equal failed.ExitCode 64 ("Late schema lookup follows " + source)

        Expect.equal
            failed.StandardOutput.Length
            0
            "Human failure does not fabricate machine output"

        let text = Encoding.UTF8.GetString failed.StandardError
        Expect.stringContains text "Nezināma komanda" "Independent Latvian failure expectation"

        Expect.stringContains
            text
            "unknown-l7-native"
            "Localized failure retains the authored selector"

        Expect.isFalse
            (text.Contains(fixture.Directory, StringComparison.Ordinal))
            "Public profile location is never exposed"

        let machine =
            ProcessRunner.dotnet 8_000 fixture.Cli [ "schema"; "invocation" ] selected None

        Expect.equal machine.ExitCode 0 "Successful machine schema is not localized"
        Expect.equal machine.StandardError.Length 0 "Successful schema has no human diagnostics"

        Expect.equal
            machine.StandardOutput
            expected.StandardOutput
            "Language selection never changes exact schema JSON bytes"

    let illegal =
        ProcessRunner.dotnet
            8_000
            fixture.Cli
            [ "schema"; "unknown-l7-native"; "--language"; "lv" ]
            (environment profileAr [])
            None

    Expect.equal illegal.ExitCode 64 "Schema still accepts no language flag"
    Expect.equal illegal.StandardOutput.Length 0 "Illegal flags leave machine stdout empty"

    Expect.stringContains
        (Encoding.UTF8.GetString illegal.StandardError)
        "Schema pieņem"
        "The existing illegal-option usage error still follows explicit language"

let qualify (fixture: PublishedFixture.Fixture) =
    configurationRefusals fixture
    configurationGuidance fixture
    schemaFailureLanguage fixture
    CliHelp.qualify fixture.Cli
    let environment = Dictionary<string, string>()

    let invoke args input =
        ProcessRunner.dotnet 8_000 fixture.Cli args environment input

    // CliInvocationTests checks the full topic/language product; publication checks all topics
    // once, and the summaries/repair routes below exercise every language's packaged resources.
    for language in [ "en" ] do
        for topic in CliTopic.all do
            let result = invoke [ "help"; CliTopic.token topic; "--language"; language ] None

            Expect.equal result.ExitCode 0 "All topics work offline"
            Expect.equal result.StandardError.Length 0 "Human help has no errors"
            Expect.isGreaterThan result.StandardOutput.Length 20 "Useful published help"

    let machineHelp = invoke [ "help"; "exit-codes"; "--format"; "json" ] None
    Expect.equal machineHelp.ExitCode 0 "Machine help works offline"
    use helpDocument = JsonDocument.Parse machineHelp.StandardOutput
    let codes = helpDocument.RootElement.GetProperty("exitCodes")
    Expect.equal (codes.GetArrayLength()) 7 "Published exit-code topic contains every status"
    Expect.equal (codes[4].GetProperty("value").GetInt32()) 64 "Usage exit status"

    for commandName in CliCommand.all do
        let command = CliCommand.token commandName

        for alias in [ "--help"; "-h" ] do
            let result = invoke [ command; alias ] None
            Expect.equal result.ExitCode 0 "Alias never opens stdin/config/network"
            let canonical = invoke [ "help"; command ] None

            Expect.equal
                result.StandardOutput
                canonical.StandardOutput
                "Alias emits canonical help bytes"

    let canary = "secret-canary\u001b[31mhttps://private.example/path"

    for args, echoSelector in
        [
            [ canary ], true
            [ "help"; canary ], true
            [ "call"; "--help"; canary ], false
            [ "help"; "fields"; canary ], true
            [ "schema"; canary ], true
        ] do
        let result = invoke args None
        Expect.equal result.ExitCode 64 "Usage failure"
        Expect.equal result.StandardOutput.Length 0 "No machine stdout on invalid invocation"

        let error = Encoding.UTF8.GetString result.StandardError
        Expect.isFalse (error.Contains '\u001b') "Usage never emits an active ANSI escape"
        Expect.isFalse (error.Contains canary) "The raw control-bearing token is never echoed"

        if echoSelector then
            Expect.stringContains
                error
                "secret-canary\\u{001B}[31mhttps://private.example/path"
                "The authored unknown selector is retained as independent inert text"
        else
            Expect.isFalse
                (error.Contains "secret-canary")
                "Closed call invocation keeps its existing argument refusal"

    missingConfiguration invoke
