module internal ClaimCore.AcceptanceTests.CliNativeOutput

open ClaimCore.Cli

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport
open CliCalls

let private run
    (fixture: PublishedFixture.Fixture)
    (environment: IReadOnlyDictionary<string, string>)
    command
    destination
    source
    : ProcessRunner.Result =
    let directory = Path.Combine(fixture.Directory, "native-output-" + id ())
    Directory.CreateDirectory directory |> ignore

    let result =
        ClaimCore.TestSupport.OutputControls.run
            directory
            fixture.Cli
            command
            {
                Destination = destination
                Environment = environment
            }
            source

    {
        ExitCode = result.ExitCode
        StandardOutput = result.StandardOutput
        StandardError = result.StandardError
    }

let private authored (fixture: PublishedFixture.Fixture) =
    let request =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = request["input"] |> nonNull
    let operation = id ()
    let reference = "NATIVE-OUTPUT-" + id ()
    input["operationId"] <- JsonValue.Create operation

    (input["inputs"] |> nonNull)["field:caseReference"] <- JsonValue.Create reference

    let values = input["inputs"] |> nonNull
    values["field:policyNumber"] <- JsonValue.Create "Rīga مطالبة 😀"
    Encoding.UTF8.GetBytes(request.ToJsonString()), operation, reference

let private head (fixture: PublishedFixture.Fixture) =
    let result = fixture.Administration [ "head" ]
    Expect.equal result.ExitCode 0 "Authoritative head is available"
    use document = JsonDocument.Parse result.StandardOutput
    document.RootElement.GetProperty("operationSequence").GetInt64()

let private accepted fixture reference =
    let current =
        automation
            fixture
            "case.get"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = reference
                    |}
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal (current.GetProperty("revision").GetInt64()) 1L "One accepted case revision"

    let history =
        automation
            fixture
            "case.timeline"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = reference
                    |}
                filter = "FACTS"
                limit = 50
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal (history.GetProperty("items").GetArrayLength()) 1 "Exactly one accepted operation"

let private fallback (operation: string) (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode 4 "Undeliverable mutation confirmation remains uncertain"
    let lines = Encoding.UTF8.GetString(result.StandardError).TrimEnd('\n').Split('\n')
    Expect.equal lines.Length 3 "Fallback reply, guidance, and exact request frame"
    Expect.stringContains lines[1] "claimcore call" "Exact resending guidance"
    Expect.stringContains lines[1] "claimcore operation get" "Final lookup guidance"

    let frame =
        Frames.decode (Encoding.UTF8.GetBytes lines[0])
        |> Result.defaultWith (fun _ -> failtest "Fallback uses the shared typed frame codec")

    Expect.equal frame.Delivery DeliveryState.Uncertain "No false confirmation"
    Expect.equal frame.OperationId (Some(Guid.Parse operation)) "Original caller identity retained"

    let retry =
        Frames.admit (Encoding.UTF8.GetBytes lines[2])
        |> Result.defaultWith (fun _ -> failtest "Last stderr line is a retryable request")

    Expect.equal (Frames.operationId retry) frame.OperationId "Exact retry identity retained"

let private mutations fixture =
    let destinations =
        [ "broken"; "both" ] @ (if OperatingSystem.IsLinux() then [ "full" ] else [])

    for command in [ "call"; "session" ] do
        for destination in destinations do
            let source, operation, reference = authored fixture
            let later, _, laterReference = authored fixture

            let input =
                if command = "session" then
                    Array.concat [ source; [| 10uy |]; later; [| 10uy |] ]
                else
                    source

            let before = head fixture
            let result = run fixture fixture.Environment command destination input

            if destination = "both" then
                Expect.equal result.ExitCode 4 "Both broken sinks still leave exit four"
                Expect.isEmpty result.StandardError "No writable confirmation channel"
            else
                fallback operation result

            Expect.equal
                (head fixture)
                (before + 1L)
                "Output failure cannot dispatch a later session frame"

            accepted fixture reference

            if command = "session" then
                automation
                    fixture
                    "case.get"
                    {|
                        target =
                            {|
                                kind = "REFERENCE"
                                caseReference = laterReference
                            |}
                    |}
                    2
                |> require "REJECTED"
                |> ignore

    Expect.equal
        (fixture.Administration [ "verify" ]).ExitCode
        0
        "Ledger and projections independently reconstruct"

let private noMutation fixture =
    let before = head fixture

    for destination in [ "broken"; "both" ] do
        let result =
            run fixture fixture.Environment "call" destination (frame "context" {| |})

        Expect.equal result.ExitCode 0 "Closed stdout on a read ends quietly"
        Expect.isEmpty result.StandardError "No misleading failure diagnostic"

    let missing = Dictionary<string, string>()
    let result = run fixture missing "call" "broken" (frame "context" {| |})
    Expect.equal result.ExitCode 0 "A closed stdout read does not become an uncertain mutation"

    for command in [ "help"; "version" ] do
        let result = run fixture missing command "broken" [||]
        Expect.equal result.ExitCode 0 "Closed discovery stdout is quiet"
        Expect.isEmpty result.StandardError "No before-dispatch diagnostic"

    Expect.isTrue
        ((run fixture missing "unsupported.synthetic" "stderr-broken" [||]).ExitCode <> 0)
        "Stderr failures remain observable"

    Expect.equal (head fixture) before "Read and discovery controls append nothing"

let private unicodeFrames fixture =
    let source, _, reference = authored fixture
    CliCalls.run fixture fixture.Environment source 0 |> ignore

    let request =
        frame
            "case.get"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = reference
                    |}
            |}

    let result =
        CliCalls.invoke
            fixture
            fixture.Environment
            [ "session" ]
            (Array.concat [ request; [| 10uy |]; request; [| 10uy |] ])
            0

    Expect.equal result.ExitCode 0 "Normal multiframe session succeeds"

    let rows =
        Encoding.UTF8
            .GetString(result.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)

    Expect.equal rows.Length 2 "One complete newline-delimited frame per read"

    let stableRead (text: string) =
        let frame = JsonNode.Parse(text) |> nonNull

        let basis =
            [ "result"; "outcome"; "data"; "work"; "basis" ]
            |> List.fold (fun (current: JsonNode) key -> current[key] |> nonNull) frame

        Expect.isTrue
            (basis.AsObject().Remove "observedAt")
            "Each read declares its own database observation"

        frame.ToJsonString()

    Expect.equal
        (stableRead rows[0])
        (stableRead rows[1])
        "Repeated frames preserve all Unicode facts across separate read observations"

    Expect.equal
        result.StandardOutput[result.StandardOutput.Length - 1]
        10uy
        "Final newline is retained"

    for row in rows do
        Expect.isOk
            (Frames.decode (Encoding.UTF8.GetBytes row))
            "Unicode machine bytes remain admitted"

let private humanActorData (fixture: PublishedFixture.Fixture) =
    let subject =
        "synthetic-\u001B[31m\r\n\t\b\u0085\u202Eالعربية\u200C\u200D-literal\\u{001B}"

    let operation = id ()
    let owner = interactiveEnvironment fixture "synthetic-owner" "valid"

    let registered =
        CliCalls.verb
            fixture
            owner
            [
                "people"
                "register"
                "--operation-id"
                operation
                "--principal-kind"
                "human"
                "--issuer"
                fixture.Issuer
                "--subject"
                subject
                "--name"
                "Safety"
            ]
            0
        |> require "ACCEPTED"

    let target = registered.GetProperty("subject").GetProperty "targetActor"

    Expect.equal
        (target.GetProperty("kind").GetString())
        "DISCLOSED"
        "Current owner receives the authorized target reference"

    let targetActor = target.GetProperty "actor"
    let actorId = targetActor.GetProperty("actorId").GetString() |> nonNull

    Expect.equal
        actorId
        operation
        "Registration keeps its deterministic operation-derived account identity"

    Expect.equal
        (targetActor.GetProperty("displayName").GetProperty("value").GetString())
        "Safety"
        "Authored profile owns the readable account name"

    let machine =
        CliCalls.verb fixture owner [ "people"; "grants"; actorId; "--limit"; "50" ] 0
        |> require "SUCCEEDED"

    let found = machine.GetProperty "actor"

    Expect.equal
        (found.GetProperty("actor").GetProperty("actorId").GetString())
        actorId
        "Technical disclosure is for the already-known registered identity"

    Expect.isTrue
        (found.GetProperty("principal").GetProperty("subject").GetString() = subject)
        "The real service and JSON CLI preserve the exact principal identity"

    let listEnvironment = Dictionary<string, string>(owner)
    listEnvironment["COLUMNS"] <- "100"

    let primary =
        CliCalls.invoke
            fixture
            listEnvironment
            [ "people"; "list"; "--limit"; "50"; "--format"; "text" ]
            Array.empty
            0

    let primaryText = Encoding.UTF8.GetString primary.StandardOutput

    let primaryRows = primaryText.Split('\n')

    let nameFirst =
        primaryRows
        |> Array.exists (fun line -> line.StartsWith("Safety  ", StringComparison.Ordinal))

    let identityFirst =
        primaryRows
        |> Array.exists (fun line ->
            line.StartsWith("synthetic-", StringComparison.Ordinal)
            || line.StartsWith(actorId, StringComparison.Ordinal))

    if not nameFirst || identityFirst then
        FixtureFiles.write fixture.Directory "account-primary-name.stdout" primary.StandardOutput
        |> ignore

    Expect.isTrue nameFirst "Primary rows identify the account by its exact authored name"

    Expect.isFalse identityFirst "Neither the unsafe principal nor ID is the leading name"

    // Generic rendering consumes the supplied HUMAN screen, not principal facts from DATA.
    fixture.TransformNextReply(
        "/api/people.grants",
        fun bytes ->
            let root = JsonNode.Parse(bytes: byte array) |> nonNull

            ((root["outcome"] |> nonNull)["document"] |> nonNull)["screen"] <-
                JsonSerializer.SerializeToNode
                    {|
                        regions =
                            [|
                                {|
                                    role = "DETAILS"
                                    titleId = "label.recordDetails"
                                    content =
                                        {|
                                            kind = "ENTRIES"
                                            entries =
                                                [|
                                                    {|
                                                        key = "technical-subject"
                                                        labelId = "field.principal.subject.label"
                                                        prominence = "DETAIL"
                                                        value =
                                                            {|
                                                                kind = "IDENTIFIER"
                                                                value = subject
                                                            |}
                                                    |}
                                                |]
                                        |}
                                |}
                            |]
                    |}

            Encoding.UTF8.GetBytes(root.ToJsonString())
    )

    let rendered =
        CliCalls.invoke
            fixture
            owner
            [
                "people"
                "grants"
                actorId
                "--limit"
                "50"
                "--format"
                "text"
                "--details"
            ]
            Array.empty
            0

    Expect.equal rendered.ExitCode 0 "The current HUMAN document is admitted and rendered"
    let text = Encoding.UTF8.GetString rendered.StandardOutput

    let row =
        text.Split('\n')
        |> Array.find (fun line -> line.Contains("synthetic-", StringComparison.Ordinal))

    for control in [ '\u001B'; '\r'; '\t'; '\b'; '\u0085'; '\u202E' ] do
        Expect.isFalse
            (primaryText.Contains control || text.Contains control)
            "Neither human table nor technical card emits untrusted terminal instructions"

    Expect.isTrue
        (row.Contains(
            "\\u{001B}[31m\\u{000D}\\u{000A}\\u{0009}\\u{0008}\\u{0085}\\u{202E}",
            StringComparison.Ordinal
        ))
        "Independent visible terminal escape profile"

    Expect.isTrue
        (row.Contains("العربية\u200C\u200D", StringComparison.Ordinal))
        "Arabic and joining characters remain readable"

    Expect.isTrue
        (row.Contains("literal\\\\u{001B}", StringComparison.Ordinal))
        "Literal escape-like strings remain distinguishable"

let qualify fixture =
    let source, _, reference = authored fixture
    CliNarrativeOutput.qualify fixture source reference (fun () -> head fixture)
    humanActorData fixture
    mutations fixture
    noMutation fixture
    unicodeFrames fixture

let publishedOutput =
    let run () =
        task {
            use fixture = new PublishedFixture.Fixture()
            do! fixture.Start()
            setup fixture
            qualify fixture
            fixture.Complete()
        }

    testCaseAsync
        "published CLI output preserves supplied screens and terminal safety"
        (async { do! run () |> Async.AwaitTask })

let summaryCsv (fixture: PublishedFixture.Fixture) =
    let prefix = "CSVFIELD-" + id ()
    let reference index = prefix + "-" + string index
    let start = DateOnly(2018, 1, 1)

    for index in 0..50 do
        let received =
            start
                .AddMonths(index)
                .ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)

        automation
            fixture
            "operation.execute"
            {|
                operationId = id ()
                task = "case.open"
                binding =
                    {|
                        kind = "CASE"
                        expectedRevision = 0
                    |}
                inputs =
                    dict
                        [
                            "field:caseReference", reference index
                            "field:receivedDate", received
                            "field:claimedAmount", "1"
                            "field:claimedCurrency", "EUR"
                        ]
            |}
            0
        |> require "ACCEPTED"
        |> ignore

    let header =
        "group,count,currency,claimed,claimedCases,payable,payableCases,netPaid,netPaidCases"

    let runSummary filter flags =
        let result =
            invoke
                fixture
                fixture.Environment
                ([ "case"; "summary"; "--format"; "csv"; "--text"; filter ] @ flags)
                Array.empty
                0

        Expect.equal result.ExitCode 0 "Published summary CSV succeeds"

        Expect.isFalse
            (Encoding.UTF8.GetString(result.StandardError).Contains("INTERNAL"))
            "No local dispatch failure"

        let rows =
            Encoding.UTF8
                .GetString(result.StandardOutput)
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)

        Expect.equal rows[0] header "One canonical CSV header"
        rows

    Expect.equal
        (runSummary (reference 50) [])
        [| header; "ALL_CASES,1,EUR,1,1,,,," |]
        "One healthy ungrouped row"

    Expect.equal
        (runSummary (reference 50) [ "--group-by"; "received-month" ])
        [| header; "2022-03,1,EUR,1,1,,,," |]
        "Grouped CSV matches the HTTP row independently"

    let page = runSummary prefix [ "--group-by"; "received-month" ]
    Expect.equal page.Length 51 "Without --all only the first 50 data rows are emitted"
    Expect.equal (Array.last page) "2022-02,1,EUR,1,1,,,," "The first-page boundary"

    let all = runSummary prefix [ "--group-by"; "received-month"; "--all" ]
    Expect.equal all.Length 52 "The established paging option emits all 51 rows"
    Expect.equal all[1] "2018-01,1,EUR,1,1,,,," "The first HTTP-equivalent record"
    Expect.equal (Array.last all) "2022-03,1,EUR,1,1,,,," "The final HTTP-equivalent record"

let publishedRelatedDirections =
    testCaseAsync
        "published CLI renders outgoing incoming and combined related directions"
        (async {
            let check () =
                task {
                    use fixture = new PublishedFixture.Fixture()
                    do! fixture.Start()
                    setup fixture
                    let prefix = "NATIVELINK-" + String.replicate 47 "R"
                    let a, b, c = prefix + "-A", prefix + "-B", prefix + "-C"
                    let hidden = "UNREADABLE-RELATED-" + id ()
                    let cases = [ a; b; c; hidden ] |> List.map (fun reference -> reference, id ())

                    for reference, operation in cases do
                        automation
                            fixture
                            "operation.execute"
                            {|
                                operationId = operation
                                task = "case.open"
                                binding =
                                    {|
                                        kind = "CASE"
                                        expectedRevision = 0
                                    |}
                                inputs = dict [ "field:caseReference", reference ]
                            |}
                            0
                        |> require "ACCEPTED"
                        |> ignore

                    let owner = interactiveEnvironment fixture "synthetic-owner" "valid"

                    let link source target =
                        let request =
                            frame
                                "operation.execute"
                                {|
                                    operationId = id ()
                                    task = "link.add"
                                    binding =
                                        {|
                                            kind = "LINK"
                                            target =
                                                {|
                                                    kind = "REFERENCE"
                                                    caseReference = source
                                                |}
                                        |}
                                    inputs = dict [ "input:relatedCaseReference", target ]
                                |}

                        CliCalls.run fixture owner request 0 |> require "ACCEPTED" |> ignore


                    let read reference =
                        let result =
                            invoke
                                fixture
                                owner
                                [
                                    "case"
                                    "get"
                                    "--format"
                                    "text"
                                    "--language"
                                    "en"
                                    "--details"
                                    reference
                                ]
                                Array.empty
                                0

                        Expect.equal
                            result.ExitCode
                            0
                            "The published CLI admits this HUMAN response"

                        Encoding.UTF8.GetString result.StandardOutput

                    link a b
                    let outgoing = read a
                    Expect.stringContains outgoing "Links from this case" "Outgoing group heading"
                    Expect.stringContains outgoing b "Outgoing target is rendered"
                    let incoming = read b
                    Expect.stringContains incoming "Links to this case" "Incoming group heading"
                    Expect.stringContains incoming a "The incoming source is rendered"

                    Expect.isFalse
                        (incoming.Contains("Links from this case", StringComparison.Ordinal))
                        "Adding an available action does not invent an outgoing group"

                    link b c
                    let both = read b
                    Expect.stringContains both "Links from this case" "Outgoing heading remains"
                    Expect.stringContains both "Links to this case" "Incoming heading remains"
                    Expect.stringContains both c "New target is visible"
                    Expect.stringContains both a "Incoming source remains visible"

                    link b hidden
                    Expect.stringContains (read b) hidden "An owner can see the third related case"

                    let notes = [ "First Rīga\n原文 😀"; "Second العربية\n\nFinal exact line" ]

                    for note in notes do
                        frame
                            "operation.execute"
                            {|
                                operationId = id ()
                                task = "note.add"
                                binding =
                                    {|
                                        kind = "NOTE"
                                        target =
                                            {|
                                                kind = "REFERENCE"
                                                caseReference = b
                                            |}
                                    |}
                                inputs = dict [ "input:noteText", note ]
                            |}
                        |> fun command -> CliCalls.run fixture owner command 0
                        |> require "ACCEPTED"
                        |> ignore

                    let reader = principal fixture "HUMAN" IdentityRealm.readerSubject
                    let actorId = selectedActorId fixture reader
                    setGrant fixture reader "CASE_READER" false

                    for reference in [ a; b; c ] do
                        frame
                            "operation.execute"
                            {|
                                operationId = id ()
                                task = "people.grant"
                                binding =
                                    {|
                                        kind = "PEOPLE"
                                        actorId = actorId
                                        expectedRevision = reviewedActorRevision fixture actorId
                                    |}
                                inputs =
                                    dict
                                        [
                                            "input:role", "CASE_READER"
                                            "input:scope", "CASE"
                                            "field:caseReference", reference
                                        ]
                            |}
                        |> fun command -> CliCalls.run fixture owner command 0
                        |> require "ACCEPTED"
                        |> ignore

                    let scoped = interactiveEnvironment fixture "synthetic-reader" "valid"

                    let readScoped args =
                        invoke fixture scoped args Array.empty 0

                    let data =
                        CliCalls.verb fixture scoped [ "case"; "notes"; b; "--all" ] 0
                        |> require "SUCCEEDED"

                    let canonical =
                        data.GetProperty("notes").GetProperty("items").EnumerateArray()
                        |> Seq.map (fun note -> note.GetProperty("text").GetString() |> nonNull)
                        |> Set.ofSeq

                    Expect.equal
                        canonical
                        (Set.ofList notes)
                        "DATA preserves both exact Unicode notes and line breaks"

                    let readCommand reference =
                        "claimcore case get --case-id="
                        + (List.find (fst >> (=) reference) cases |> snd)

                    for width in [ "112"; "32" ] do
                        scoped["COLUMNS"] <- width

                        let rendered args =
                            (readScoped args).StandardOutput |> Encoding.UTF8.GetString

                        let detail = rendered [ "case"; "get"; b; "--format"; "text"; "--details" ]

                        let notePage =
                            rendered
                                [ "case"; "notes"; b; "--format"; "text"; "--all"; "--details" ]

                        for note in notes do
                            Expect.stringContains detail note "Case details contain the full note"

                            Expect.stringContains
                                notePage
                                note
                                "Notes details contain the full note"

                        for reference, other in [ a, c; c, a ] do
                            let command = readCommand reference
                            let position = detail.IndexOf(command, StringComparison.Ordinal)
                            Expect.isGreaterThanOrEqual position 0 "Exact stable read command"
                            let preceding = detail.Substring(0, position)

                            Expect.isGreaterThan
                                (preceding.LastIndexOf(reference, StringComparison.Ordinal))
                                (max -1 (preceding.LastIndexOf(other, StringComparison.Ordinal)))
                                "The full reference precedes its own command in the same record"

                        Expect.isFalse
                            (detail.Contains(hidden, StringComparison.Ordinal))
                            "The unreadable reference is withheld"

                        Expect.isFalse
                            (detail.Contains(readCommand hidden, StringComparison.Ordinal))
                            "The unreadable command is withheld"

                    fixture.Complete()
                }

            do! check () |> Async.AwaitTask
        })
