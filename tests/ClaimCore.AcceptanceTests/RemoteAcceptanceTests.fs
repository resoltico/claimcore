module ClaimCore.AcceptanceTests.RemoteAcceptanceTests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Expecto

let private ownerMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
let private operationOpen = lazy (RemoteFixture.id ())
let private operationClose = lazy (RemoteFixture.id ())
let private operationReopen = lazy (RemoteFixture.id ())

let private registration =
    Map.ofList
        [
            "incidentDate", "2026-08-01"
            "incidentNotificationDate", "2026-08-03"
            "incidentCountry", "Latvia"
            "claimantName", "Synthetic published claimant"
            "insurerName", "Synthetic published insurer"
            "claimedAmount", "120.00"
            "claimedCurrency", "EUR"
        ]

let private expectTag expected (document: JsonDocument) =
    Expect.equal (RemoteFixture.tag document) expected "Exact generated service outcome"

let private result endpoint input expectedExit =
    RemoteFixture.call endpoint input |> RemoteFixture.parse expectedExit endpoint

let private discovery () =
    let empty = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>
    let cli = RemoteFixture.inputs.Value.CliDll
    let help = ProcessRunner.dotnet 30_000 cli [ "help" ] empty None
    Expect.equal help.ExitCode 0 "Help needs no credentials"
    Expect.equal help.StandardError.Length 0 "No discovery stderr"

    let schema = ProcessRunner.dotnet 30_000 cli [ "schema"; "invocation" ] empty None
    Expect.equal schema.ExitCode 0 "Generated invocation schema needs no credentials"
    use document = JsonDocument.Parse(ReadOnlyMemory schema.StandardOutput)
    Expect.isTrue (document.RootElement.TryGetProperty("$schema") |> fst) "Schema document"

let private strictFrame () =
    let old =
        Encoding.UTF8.GetBytes(
            """{"protocolVersion":3,"endpoint":"case.list","input":{"limit":1}}"""
        )

    let response = RemoteFixture.run [ "call" ] (Some old)
    Expect.equal response.ExitCode 2 "Old CLI contract refused"
    use document = JsonDocument.Parse(ReadOnlyMemory response.StandardOutput)

    Expect.equal
        (document.RootElement.GetProperty("kind").GetString())
        "protocolFailure"
        "Typed refusal"

    let duplicate =
        Encoding.UTF8.GetBytes(
            """{"protocolVersion":4,"protocolVersion":4,"endpoint":"case.list","input":{"limit":1}}"""
        )

    let repeated = RemoteFixture.run [ "call" ] (Some duplicate)
    Expect.equal repeated.ExitCode 2 "Duplicate frame refused"

let private grantedService () =
    let directory =
        RemoteFixture.inputs.Value.CliDll
        |> Path.GetDirectoryName
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Published CLI directory is missing.")

    let path = Path.Combine(directory, "ClaimCore.Cli.deps.json")
    let dependencies = File.ReadAllText(path)
    Expect.isFalse (dependencies.Contains("ClaimCore.Postgres/")) "No primary storage dependency"
    Expect.isFalse (dependencies.Contains("Npgsql/")) "No database driver dependency"

    use page = result "case.list" {| limit = 1 |} 0
    expectTag "SUCCEEDED" page

let private acceptedCommand () =
    use opened =
        RemoteFixture.command "command.execute" operationOpen.Value 0L "OPEN" registration
        |> RemoteFixture.parse 0 "command.execute"

    expectTag "COMPLETED" opened

    let data =
        opened.RootElement.GetProperty("service").GetProperty("outcome").GetProperty("data")

    Expect.equal (data.GetProperty("settlement").GetString()) "CONFIRMED" "Witness settled"

    Expect.equal
        (data.GetProperty("execution").GetProperty("tag").GetString())
        "ACCEPTED"
        "Accepted"

    use replayed =
        RemoteFixture.command "command.execute" operationOpen.Value 0L "OPEN" registration
        |> RemoteFixture.parse 0 "command.execute"

    expectTag "OBSERVED_ACCEPTED" replayed

let private observedCase () =
    use current =
        result
            "case.get"
            {|
                caseReference = RemoteFixture.caseReference.Value
            |}
            0

    expectTag "SUCCEEDED" current

    use history =
        result
            "case.history"
            {|
                caseReference = RemoteFixture.caseReference.Value
                limit = 50
                detail = "FULL"
            |}
            0

    expectTag "SUCCEEDED" history

    use page = result "case.list" {| limit = 50 |} 0
    expectTag "SUCCEEDED" page

let private preparedAndResolved () =
    use prepared =
        RemoteFixture.command "command.prepare" operationClose.Value 1L "CLOSE" Map.empty
        |> RemoteFixture.parse 0 "command.prepare"

    expectTag "PREPARED" prepared

    let digest =
        prepared.RootElement
            .GetProperty("service")
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("details")
            .GetProperty("summary")
            .GetProperty("requestSha256")
            .GetString()

    use resolved =
        result
            "recovery.resolve"
            {|
                operationId = operationClose.Value
                requestSha256 = digest
            |}
            0

    expectTag "COMPLETED" resolved

let private privateArtifact () =
    use prepared =
        RemoteFixture.command "command.prepare" operationReopen.Value 2L "REOPEN" Map.empty
        |> RemoteFixture.parse 0 "command.prepare"

    expectTag "PREPARED" prepared

    let digest =
        prepared.RootElement
            .GetProperty("service")
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("details")
            .GetProperty("summary")
            .GetProperty("requestSha256")
            .GetString()

    let destination =
        Path.Combine(
            RemoteFixture.inputs.Value.PrivateDirectory,
            "cli-export-" + RemoteFixture.id () + ".json"
        )

    let exported =
        RemoteFixture.call
            "recovery.export"
            {|
                operationId = operationReopen.Value
                requestSha256 = digest
                destination = destination
            |}

    Expect.equal exported.ExitCode 0 "Private export succeeded"
    Expect.isTrue (File.Exists(destination)) "Exclusive private file was created"
    RemoteFixture.observedDestinationFailure operationReopen.Value digest destination

    use preview = result "recovery.importEnvelopePreview" {| source = destination |} 0
    expectTag "SUCCEEDED" preview

    let sourceDigest =
        preview.RootElement
            .GetProperty("service")
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("sourceSha256")
            .GetString()

    use retained =
        result
            "recovery.importEnvelopeRetain"
            {|
                source = destination
                sourceSha256 = sourceDigest
                confirmed = true
            |}
            0

    let tag = RemoteFixture.tag retained
    Expect.isTrue (tag = "EXISTING" || tag = "RETAINED") "Exact private artifact retained"

let private privateRefusal () =
    let directory = RemoteFixture.inputs.Value.PrivateDirectory
    let target = Path.Combine(directory, "cli-private-" + RemoteFixture.id () + ".json")
    let linked = Path.Combine(directory, "cli-link-" + RemoteFixture.id () + ".json")

    use stream =
        new FileStream(
            target,
            FileStreamOptions(
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = Nullable ownerMode
            )
        )

    stream.Write(Encoding.UTF8.GetBytes("{}"))
    stream.Flush(true)
    File.CreateSymbolicLink(linked, target) |> ignore

    let response =
        RemoteFixture.call "recovery.importEnvelopePreview" {| source = linked |}

    Expect.equal response.ExitCode 3 "Symlink source refused before service dispatch"
    let output = Encoding.UTF8.GetString(response.StandardOutput)
    Expect.isFalse (output.Contains(linked, StringComparison.Ordinal)) "No path disclosure"
    use document = JsonDocument.Parse(ReadOnlyMemory response.StandardOutput)

    Expect.equal
        (document.RootElement.GetProperty("kind").GetString())
        "localFailure"
        "Local refusal"

let private sessionFrames () =
    let valid = RemoteFixture.encode "case.list" {| limit = 1 |}
    let frames = Encoding.UTF8.GetBytes(valid + "\n{\n")
    let response = RemoteFixture.run [ "session" ] (Some frames)
    Expect.equal response.ExitCode 0 "NDJSON session exit"

    let lines =
        Encoding.UTF8.GetString(response.StandardOutput).TrimEnd('\n').Split('\n')

    Expect.equal lines.Length 2 "One response per input frame"
    use first = JsonDocument.Parse(lines[0])
    use second = JsonDocument.Parse(lines[1])
    Expect.equal (first.RootElement.GetProperty("kind").GetString()) "result" "First frame"

    Expect.equal
        (second.RootElement.GetProperty("kind").GetString())
        "protocolFailure"
        "Second frame"

let tests =
    testList
        "published authenticated CLI v4 acceptance"
        [
            testCase "configuration-free published discovery" discovery
            testCase "strict CLI v4 frame refusal" strictFrame
            testCase "service credential reads without a database dependency" grantedService
            testCase "[CC-CLI-001] witnessed accepted command and exact replay" acceptedCommand
            testCase "authenticated case list, get, and history" observedCase
            testCase "retained preparation resolves by exact digest" preparedAndResolved
            testCase "[CC-CLI-002] private recovery export and preview" privateArtifact
            testCase
                "[CC-CLI-002] unsafe private source refuses without path disclosure"
                privateRefusal
            testCase "NDJSON session preserves frame-local results" sessionFrames
            testCase
                "forged loopback PKCE state is refused before service access"
                RemoteAuthAcceptance.forgedPkceCallback
            testCase
                "[CC-CLI-001][CC-AUTH-001] native public-client grants replay and read back the existing owner"
                NativeOwnerGrantAcceptance.qualify
            yield! RemoteProtocolBoundaryTests.tests
        ]
    |> testSequenced
