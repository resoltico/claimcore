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

let private deliveryFields exitCode (reply: JsonElement) =
    // Caller consumers require the breaking frame metadata even when values are null.
    reply.GetProperty("endpoint") |> ignore
    reply.GetProperty("operationId") |> ignore
    let knowledge = reply.GetProperty("attemptKnowledge").GetString()
    let evidence = reply.GetProperty("resolutionEvidence").GetString()

    Expect.contains
        [ "NONE"; "CONTENT_ACCEPTED"; "CONTENT_EXCLUDED" ]
        evidence
        "Closed content evidence"

    match knowledge with
    | "UNCONFIRMED" -> Expect.equal exitCode 4 "Unconfirmed attempts stop automation"
    | "ACCEPTED" ->
        Expect.equal exitCode 0 "Accepted content succeeds"
        Expect.equal evidence "CONTENT_ACCEPTED" "Acceptance has exact-content evidence"
    | "READ_SUCCEEDED" -> Expect.equal exitCode 0 "A successful read has no mutation uncertainty"
    | "NOT_SENT"
    | "NOT_ACCEPTED"
    | "READ_FAILED" ->
        Expect.contains [ 2; 3; 70; 130 ] exitCode "Definite attempt failure has a matching exit"
    | _ -> failtest "Missing or unsupported per-attempt delivery knowledge"

let parse code (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode code "Published CLI exit"
    Expect.equal result.StandardError.Length 0 "No private diagnostics"
    use document = JsonDocument.Parse(ReadOnlyMemory result.StandardOutput)
    let reply = document.RootElement.Clone()
    deliveryFields code reply
    reply

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

let installation = {| kind = "INSTALLATION" |}

let private grantFrame target role =
    frame
        "authority.execute"
        {|
            operationId = id ()
            target = target
            act =
                {|
                    kind = "SET_GRANT"
                    role = role
                    scope = installation
                    active = true
                |}
        |}

let setGrant fixture target role active =
    let source =
        frame
            "authority.execute"
            {|
                operationId = id ()
                target = target
                act =
                    {|
                        kind = "SET_GRANT"
                        role = role
                        scope = installation
                        active = active
                    |}
            |}

    run fixture (interactiveEnvironment fixture "synthetic-owner" "valid") source 0
    |> require "ACCEPTED"
    |> ignore

let grant fixture target role = setGrant fixture target role true

let private setupFrames fixture =
    let registrations =
        [
            "SERVICE", "service", [ "CASE_HANDLER"; "PAYMENT_RECORDER" ]
            "HUMAN", IdentityRealm.stewardSubject, [ "DATA_STEWARD" ]
            "HUMAN", IdentityRealm.readerSubject, [ "CASE_READER" ]
            "HUMAN", IdentityRealm.editorSubject, [ "CASE_HANDLER"; "PAYMENT_RECORDER" ]
        ]
        |> List.collect (fun (kind, subject, roles) ->
            let target = principal fixture kind subject

            frame
                "authority.execute"
                {|
                    operationId = id ()
                    target = target
                    act = {| kind = "REGISTER_ACTOR" |}
                |}
            :: (roles |> List.map (grantFrame target)))

    registrations
    @ ([ "CASE_HANDLER"; "PAYMENT_RECORDER" ]
       |> List.map (grantFrame (principal fixture "HUMAN" IdentityRealm.ownerSubject)))

let internal requireAcceptedReplies expected (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode 0 "Published CLI session exit"
    Expect.equal result.StandardError.Length 0 "No private diagnostics"
    let lines = UTF8Encoding(false, true).GetString(result.StandardOutput).Split('\n')
    Expect.equal lines[lines.Length - 1] "" "Every reply ends with a newline"
    Expect.equal (lines.Length - 1) expected "One reply for every authority frame"

    for line in lines[.. lines.Length - 2] do
        use document = JsonDocument.Parse(line)
        let reply = document.RootElement
        let receipt = reply |> require "ACCEPTED"

        Expect.equal
            (reply.GetProperty("kind").GetString())
            "result"
            "Setup returns a protocol result"

        Expect.equal
            (reply.GetProperty("endpoint").GetString())
            "authority.execute"
            "Authority setup endpoint"

        Expect.equal
            (reply.GetProperty("attemptKnowledge").GetString())
            "ACCEPTED"
            "Explicit setup acceptance"

        Expect.equal
            (reply.GetProperty("resolutionEvidence").GetString())
            "CONTENT_ACCEPTED"
            "Exact setup content is confirmed"

        Expect.equal
            (reply.GetProperty("operationId").GetString())
            (receipt.GetProperty("operationId").GetString())
            "Setup receipt correlation"

let setup fixture =
    let frames = setupFrames fixture

    let source =
        frames
        |> List.map (fun value -> Array.append value [| byte '\n' |])
        |> Array.concat

    let environment = interactiveEnvironment fixture "synthetic-owner" "valid"

    ProcessRunner.dotnet 90_000 fixture.Cli [ "session" ] environment (Some source)
    |> requireAcceptedReplies frames.Length
