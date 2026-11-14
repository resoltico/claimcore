module internal ClaimCore.AcceptanceTests.CliCalls

open ClaimCore.Domain

open ClaimCore.TestSupport

open ClaimCore.Cli

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Expecto

let caseSubmission (operation: string) reference revision act =
    let command =
        Inputs.caseOperation
            {
                OperationId = Guid.Parse operation
                CaseReference = reference
                ExpectedRevision = revision
                Act = act
            }

    let bytes =
        ClaimCore.Json.Codec.encode
            ClaimCore.Wire.OperationSubmissionCodecs.submission
            (Submissions.ofCommand None command)

    use document = JsonDocument.Parse bytes
    document.RootElement.Clone()

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
        let pending =
            match reply.GetProperty("result").TryGetProperty "outcome" with
            | true, outcome -> outcome.GetProperty("kind").GetString() = "PENDING"
            | _ -> false

        Expect.equal
            delivery
            (if pending then "DELIVERED" else "UNCERTAIN")
            "Pending reads and uncertain submissions remain distinct"

    if exitCode = 0 then
        Expect.equal delivery "DELIVERED" "Success was delivered"

let parse code (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode code "Published CLI exit"

    let admitted =
        ClaimCore.Cli.Frames.decode result.StandardOutput
        |> Result.defaultWith (fun _ ->
            failtest "Published CLI output is an admitted closed reply frame")

    if code = 4 && admitted.Delivery = ClaimCore.Cli.DeliveryState.Uncertain then
        let lines =
            UTF8Encoding(false, true).GetString(result.StandardError).TrimEnd('\n').Split('\n')

        let retry = Encoding.UTF8.GetBytes lines[lines.Length - 1]

        Expect.isOk
            (ClaimCore.Cli.Frames.admit retry)
            "Uncertain delivery retains the exact retry frame"
    elif result.StandardError.Length <> 0 then
        let actual = UTF8Encoding(false, true).GetString(result.StandardError)

        let expected =
            admitted.OperationId |> Option.map (fun value -> value.ToString("D") + "\n")

        Expect.equal (Some actual) expected "An ID-less call prints only its dispatched UUID"

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

// Resolve before the CLI isolates XDG state: version-manager shims may store their trust there.
let private nodeExecutable =
    lazy
        (let result =
            PublishedProcesses.run
                10000
                (PublishedProcesses.start
                    "node"
                    [ "-p"; "process.execPath" ]
                    (Dictionary<string, string>()))
                None

         Expect.equal
             result.ExitCode
             0
             "Resolve the configured Node runtime before isolated CLI launch"

         let path = UTF8Encoding(false, true).GetString(result.StandardOutput).Trim()

         Expect.isTrue
             (Path.IsPathFullyQualified path && File.Exists path)
             "Node runtime is an executable path"

         path)

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
    values["CLAIMCORE_CLI_TEST_NODE"] <- nodeExecutable.Value
    values["CLAIMCORE_CLI_TEST_DRIVER"] <- Path.Combine(fixture.Root, "web/e2e/cli-pkce-driver.mjs")

    let launcher =
        FixtureFiles.text
            fixture.Directory
            (if OperatingSystem.IsMacOS() then "open" else "xdg-open")
            "#!/bin/sh\nprintf 'launcher-started\\n' > \"$CLAIMCORE_CLI_TEST_PROGRESS_FILE\"\nexec \"$CLAIMCORE_CLI_TEST_NODE\" \"$CLAIMCORE_CLI_TEST_DRIVER\" \"$@\" > \"$CLAIMCORE_CLI_TEST_PROGRESS_FILE.stdout\" 2> \"$CLAIMCORE_CLI_TEST_PROGRESS_FILE.stderr\"\n"

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

let outcomeKind (reply: JsonElement) =
    reply.GetProperty("result").GetProperty("outcome").GetProperty("kind").GetString()

let data (reply: JsonElement) =
    let outcome = reply.GetProperty("result").GetProperty("outcome")

    let memberName =
        match outcome.GetProperty("kind").GetString() with
        | "REJECTED" -> "rejection"
        | "FAILED" -> "fault"
        | _ -> "data"

    outcome.GetProperty memberName

let require expected reply =
    Expect.equal (outcomeKind reply) expected "Published outcome"
    data reply

let id () = Guid.CreateVersion7().ToString("D")

let lookupReceipt fixture operationId =
    automation fixture "operation.get" {| operationId = operationId |} 0
    |> require "ACCEPTED"
    |> fun value -> value.GetProperty "receipt"

let principal (fixture: PublishedFixture.Fixture) kind subject =
    {|
        kind = kind
        issuer = fixture.Issuer
        subject = subject
    |}

let selectedActorId
    fixture
    (target:
        {|
            kind: string
            issuer: string
            subject: string
        |})
    =
    let rec find cursor =
        let page =
            match cursor with
            | None ->
                human fixture "synthetic-owner" "people.list" {| limit = 50 |} 0
                |> require "SUCCEEDED"
            | Some cursor ->
                human fixture "synthetic-owner" "people.list" {| limit = 50; cursor = cursor |} 0
                |> require "SUCCEEDED"

        let actor =
            page.GetProperty("items").EnumerateArray()
            |> Seq.tryFind (fun item ->
                let principal = item.GetProperty("principal")

                principal.GetProperty("kind").GetString() = target.kind
                && principal.GetProperty("issuer").GetString() = target.issuer
                && principal.GetProperty("subject").GetString() = target.subject)

        match actor with
        | Some actor -> actor.GetProperty("actor").GetProperty("actorId").GetString() |> nonNull
        | None ->
            match page.GetProperty("nextCursor").GetString() with
            | null -> failtest "Synthetic target must be registered before actor selection."
            | cursor -> find (Some cursor)

    find None

let private grantFrame actorId role =
    frame
        "operation.execute"
        {|
            operationId = id ()
            task = "people.grant"
            binding = {| kind = "PEOPLE"; actorId = actorId |}
            inputs = dict [ "input:role", role; "input:scope", "INSTALLATION" ]
        |}

let setGrant fixture target role active =
    let actorId = selectedActorId fixture target

    let source =
        frame
            "operation.execute"
            {|
                operationId = id ()
                task = (if active then "people.grant" else "people.revoke")
                binding = {| kind = "PEOPLE"; actorId = actorId |}
                inputs = dict [ "input:role", role; "input:scope", "INSTALLATION" ]
            |}

    run fixture (interactiveEnvironment fixture "synthetic-owner" "valid") source 0
    |> require "ACCEPTED"
    |> ignore

let grant fixture target role = setGrant fixture target role true

let private setupFrames (fixture: PublishedFixture.Fixture) =
    let registrations =
        [
            "SERVICE", "service", "Published service", [ "CASE_HANDLER"; "PAYMENT_RECORDER" ]
            "HUMAN", IdentityRealm.stewardSubject, "Published steward", [ "DATA_STEWARD" ]
            "HUMAN", IdentityRealm.readerSubject, "Published reader", [ "CASE_READER" ]
            "HUMAN",
            IdentityRealm.editorSubject,
            "Published editor",
            [ "CASE_HANDLER"; "PAYMENT_RECORDER" ]
        ]
        |> List.collect (fun (kind, subject, displayName, roles) ->
            let operation = id ()

            frame
                "operation.execute"
                {|
                    operationId = operation
                    task = "people.register"
                    binding = {| kind = "PEOPLE" |}
                    inputs =
                        dict
                            [
                                "input:principal.kind", box kind
                                "input:principal.issuer", box fixture.Issuer
                                "input:principal.subject", box subject
                                "input:profile.displayName", box displayName
                            ]
                |}
            :: (roles |> List.map (grantFrame operation)))

    registrations

let internal requireAcceptedReplies expected (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode 0 "Published CLI session exit"
    Expect.equal result.StandardError.Length 0 "No private diagnostics"
    let lines = UTF8Encoding(false, true).GetString(result.StandardOutput).Split('\n')
    Expect.equal lines[lines.Length - 1] "" "Every reply ends with a newline"
    Expect.equal (lines.Length - 1) expected "One reply for every people frame"

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
            "Setup receipt identity"

let setup fixture =
    let frames = setupFrames fixture

    let source =
        frames
        |> List.map (fun value -> Array.append value [| byte '\n' |])
        |> Array.concat

    let environment = interactiveEnvironment fixture "synthetic-owner" "valid"

    invoke fixture environment [ "session" ] source 0
    |> requireAcceptedReplies frames.Length
