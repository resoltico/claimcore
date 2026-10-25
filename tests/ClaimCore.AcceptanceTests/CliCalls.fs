module internal ClaimCore.AcceptanceTests.CliCalls

open ClaimCore.Cli

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
    let delivery = reply.GetProperty("delivery").GetString()
    Expect.contains [ "NOT_SENT"; "DELIVERED"; "UNCERTAIN" ] delivery "Closed delivery state"

    if exitCode = 4 then
        Expect.equal delivery "UNCERTAIN" "Uncertainty stops automation"

    if exitCode = 0 then
        Expect.equal delivery "DELIVERED" "Success was delivered"

let parse code (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode code "Published CLI exit"

    if code = 4 then
        let lines =
            UTF8Encoding(false, true).GetString(result.StandardError).TrimEnd('\n').Split('\n')

        let retry = Encoding.UTF8.GetBytes lines[lines.Length - 1]

        Expect.isOk
            (ClaimCore.Cli.Frames.admit retry)
            "The last stderr line is an exact retry frame"
    else
        Expect.equal result.StandardError.Length 0 "No private diagnostics"

    let admitted =
        ClaimCore.Cli.Frames.decode result.StandardOutput
        |> Result.defaultWith (fun _ ->
            failtest "Published CLI output is an admitted closed reply frame")

    Expect.equal (ClaimCore.Cli.Frames.exitCode admitted) code "Frame and process exits agree"
    use document = JsonDocument.Parse(ReadOnlyMemory result.StandardOutput)
    let reply = document.RootElement.Clone()
    deliveryFields code reply
    reply

let internal invoke
    (fixture: PublishedFixture.Fixture)
    (environment: IReadOnlyDictionary<string, string>)
    arguments
    source
    expected
    =
    let progress =
        match environment.TryGetValue "CLAIMCORE_CLI_TEST_PROGRESS_FILE" with
        | true, path -> Some path
        | _ -> None

    CliDriverCompletion.run 90_000 Console.Error progress expected (fun () ->
        CliInvocationEvidence.run expected Console.Error progress (fun () ->
            ProcessRunner.dotnet 90_000 fixture.Cli arguments environment (Some source)))

let run (fixture: PublishedFixture.Fixture) environment source code =
    invoke fixture environment [ "call" ] source code |> parse code

let verb fixture environment arguments code =
    invoke fixture environment (arguments @ [ "--format"; "json" ]) Array.empty code
    |> parse code

let automation fixture endpoint input code =
    run fixture fixture.Environment (frame endpoint input) code

let interactiveEnvironment (fixture: PublishedFixture.Fixture) username mode =
    let values = Dictionary<string, string>(fixture.Environment)
    values.Remove("CLAIMCORE_OIDC_CLIENT_SECRET_FILE") |> ignore
    values["CLAIMCORE_CLI_AUTH_MODE"] <- "interactive"
    values["CLAIMCORE_OIDC_CLIENT_ID"] <- "cli"
    values["CLAIMCORE_CLI_TEST_AUTH_MODE"] <- mode

    values["CLAIMCORE_CLI_TEST_PROGRESS_FILE"] <-
        Path.Combine(
            fixture.Directory,
            "cli-auth-" + mode + "-" + Guid.NewGuid().ToString("N") + ".progress"
        )

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
    reply.GetProperty("result").GetProperty("outcome").GetProperty("tag").GetString()

let data (reply: JsonElement) =
    reply.GetProperty("result").GetProperty("outcome").GetProperty("data")

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
        "operation.execute"
        {|
            scope = "AUTHORITY"
            operationId = id ()
            target = target
            act =
                {|
                    kind = "GRANT"
                    role = role
                    scope = installation
                |}
        |}

let setGrant fixture target role active =
    let source =
        frame
            "operation.execute"
            {|
                scope = "AUTHORITY"
                operationId = id ()
                target = target
                act =
                    {|
                        kind = if active then "GRANT" else "REVOKE"
                        role = role
                        scope = installation
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
                "operation.execute"
                {|
                    scope = "AUTHORITY"
                    operationId = id ()
                    target = target
                    act = {| kind = "REGISTER" |}
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
        let admitted =
            ClaimCore.Cli.Frames.decode (Encoding.UTF8.GetBytes line)
            |> Result.defaultWith (fun _ ->
                failtest "Setup output is an admitted closed reply frame")

        Expect.equal (ClaimCore.Cli.Frames.exitCode admitted) 0 "Setup frame exit"
        use document = JsonDocument.Parse(line)
        let reply = document.RootElement
        let receipt = reply |> require "ACCEPTED"

        Expect.equal
            (reply.GetProperty("endpoint").GetString())
            "operation.execute"
            "Setup endpoint"

        Expect.equal (reply.GetProperty("delivery").GetString()) "DELIVERED" "Setup delivered"

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

    invoke fixture environment [ "session" ] source 0
    |> requireAcceptedReplies frames.Length
