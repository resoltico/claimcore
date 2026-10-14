module internal ClaimCore.AcceptanceTests.CliCalls

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Expecto

let frame endpoint input =
    JsonSerializer.Serialize {| endpoint = endpoint; input = input |}
    |> Encoding.UTF8.GetBytes

let parse code (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode code "Published CLI exit"
    Expect.equal result.StandardError.Length 0 "No private diagnostics"
    use document = JsonDocument.Parse(ReadOnlyMemory result.StandardOutput)
    document.RootElement.Clone()

let run (fixture: PublishedFixture.Fixture) environment source code =
    ProcessRunner.dotnet 90_000 fixture.Cli [ "call" ] environment (Some source)
    |> parse code

let automation fixture endpoint input code =
    run fixture fixture.Environment (frame endpoint input) code

let interactiveEnvironment (fixture: PublishedFixture.Fixture) username mode =
    let values = Dictionary<string, string>(fixture.Environment)
    values.Remove("CLAIMCORE_OIDC_CLIENT_SECRET_FILE") |> ignore
    values["CLAIMCORE_CLI_AUTH_MODE"] <- "interactive"
    values["CLAIMCORE_OIDC_CLIENT_ID"] <- "cli"
    values["CLAIMCORE_CLI_TEST_AUTH_MODE"] <- mode
    values["CLAIMCORE_CLI_TEST_USERNAME"] <- username

    values["CLAIMCORE_CLI_TEST_CREDENTIALS_FILE"] <-
        Path.Combine(fixture.Directory, "credentials.json")

    values["CLAIMCORE_CLI_TEST_ISSUER"] <- fixture.Issuer
    values["CLAIMCORE_CLI_TEST_PUBLIC_CLIENT_ID"] <- "cli"
    values["CLAIMCORE_CLI_TEST_DRIVER"] <- Path.Combine(fixture.Root, "web/e2e/cli-pkce-driver.mjs")

    let launcher =
        FixtureFiles.text
            fixture.Directory
            (if OperatingSystem.IsMacOS() then "open" else "xdg-open")
            "#!/bin/sh\nexec node \"$CLAIMCORE_CLI_TEST_DRIVER\" \"$@\"\n"

    File.SetUnixFileMode(
        launcher,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )

    values["PATH"] <-
        fixture.Directory
        + string Path.PathSeparator
        + Environment.GetEnvironmentVariable("PATH")

    values

let human fixture username endpoint input code =
    run fixture (interactiveEnvironment fixture username "valid") (frame endpoint input) code

let tag (reply: JsonElement) =
    reply.GetProperty("outcome").GetProperty("tag").GetString()

let data (reply: JsonElement) =
    reply.GetProperty("outcome").GetProperty("data")

let require expected reply =
    Expect.equal (tag reply) expected "Published outcome"
    data reply

let id () = Guid.NewGuid().ToString("D")

let principal (fixture: PublishedFixture.Fixture) kind subject =
    {|
        kind = kind
        issuer = fixture.Issuer
        subject = subject
    |}

let installation = {| ``type`` = "INSTALLATION" |}

let grant fixture target role =
    human
        fixture
        "synthetic-owner"
        "authority.setGrant"
        {|
            eventId = id ()
            principal = target
            role = role
            scope = installation
            active = true
        |}
        0
    |> require "APPLIED"
    |> ignore

let setup fixture =
    for kind, subject, role in
        [
            "SERVICE", "service", "CASE_EDITOR"
            "HUMAN", IdentityRealm.stewardSubject, "DATA_STEWARD"
            "HUMAN", IdentityRealm.readerSubject, "CASE_READER"
            "HUMAN", IdentityRealm.editorSubject, "CASE_EDITOR"
        ] do
        let target = principal fixture kind subject

        human
            fixture
            "synthetic-owner"
            "authority.register"
            {|
                eventId = id ()
                principal = target
            |}
            0
        |> require "APPLIED"
        |> ignore

        grant fixture target role

    grant fixture (principal fixture "HUMAN" IdentityRealm.ownerSubject) "CASE_EDITOR"
