module ClaimCore.Tests.CaseListCommandTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Cli
open ClaimCore.TestSupport

let private moment = DateTimeOffset(2026, 10, 28, 12, 30, 0, TimeSpan.Zero)

let private facts =
    {
        Status = CaseStatus.Opened
        ClaimantName = Inputs.name "Synthetic, \"quoted\"\r\nclaimant"
        InsurerName = Inputs.name "Synthetic insurer"
        Claimed = Inputs.money "1000.00" "EUR"
        Payable = None
        Progress = CaseProgress.Undecided
    }

let private row reference =
    {
        Reference = Inputs.reference reference
        Revision = CaseFixtures.revision 1L
        ChangedAt = moment
        State = CaseState.Active facts
    }

let private page items next =
    {
        Items = items
        Offers = []
        NextCursor = next
    }

let private prepared arguments =
    Grammar.prepare (fun () -> "unused") ([ "case"; "list" ] @ arguments)
    |> Fixtures.accepted

let private run arguments (replies: Outcome<CaseSummaryPage> list) =
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let pending = prepared arguments
    let calls = ResizeArray<Invocation>()
    let remaining = Collections.Generic.Queue<Outcome<CaseSummaryPage>>(replies)

    let io =
        {
            Exchange =
                fun frame (_: CancellationToken) ->
                    calls.Add frame
                    let response = remaining.Dequeue()

                    CliReply.result frame (Api.caseList.Output.Write response)
                    |> Fixtures.accepted
                    |> Task.FromResult
            Deliver =
                fun frame reply ->
                    let bytes =
                        if pending.Format = VerbFormat.Json then
                            reply.Bytes
                        else
                            TextReply.bytesFor pending.Language (Some frame) reply.Frame

                    output.Write bytes
                    output.WriteByte(byte '\n')
            Output = fun bytes -> output.Write(bytes: byte array)
            Errors = fun bytes -> errors.Write(bytes: byte array)
        }

    let code =
        CaseListCommand.run io pending CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    code, output.ToArray(), errors.ToArray(), List.ofSeq calls

let private parseCsv (bytes: byte array) =
    use input = new StringReader(Encoding.UTF8.GetString bytes)
    use parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(input)
    parser.SetDelimiters [| "," |]
    parser.HasFieldsEnclosedInQuotes <- true
    parser.TrimWhiteSpace <- false
    let rows = ResizeArray<string list>()

    while not parser.EndOfData do
        rows.Add(parser.ReadFields() |> nonNull |> Array.toList)

    List.ofSeq rows

[<Tests>]
let tests =
    testList
        "case-list export and traversal"
        [
            testCase
                "CSV protects formula-leading references claimant and insurer values"
                (fun () ->
                    for prefix in [ "="; "+"; "-"; "@"; "\t"; "\r" ] do
                        let reference =
                            if prefix = "\t" || prefix = "\r" then
                                "SAFE"
                            else
                                prefix + "CASE"

                        let claimant = prefix + "call,\"quoted\"\r\nclaimant"
                        let insurer = prefix + "insurer"

                        let exported =
                            { row reference with
                                State =
                                    CaseState.Active
                                        { facts with
                                            ClaimantName = Inputs.name claimant
                                            InsurerName = Inputs.name insurer
                                        }
                            }

                        let code, bytes, _, _ =
                            run [ "--format"; "csv" ] [ Outcome.Succeeded(page [ exported ] None) ]

                        Expect.equal code 0 "Export succeeds without changing admission"
                        let cells = (parseCsv bytes)[1]

                        Expect.equal
                            cells[0]
                            (if reference = "SAFE" then "SAFE" else "'" + reference)
                            "Formula-leading references are protected"

                        Expect.equal
                            cells[4]
                            ("'" + claimant)
                            "Claimant protection precedes quoting"

                        Expect.equal
                            cells[5]
                            ("'" + insurer)
                            "Insurer protection uses the same rule")
            testCase
                "CSV cell protection handles whitespace and leaves ordinary cells exact"
                (fun () ->
                    for authored, expected in
                        [
                            "", ""
                            "ordinary", "ordinary"
                            "a=1+1", "a=1+1"
                            "  ordinary", "  ordinary"
                            "  =1+1", "'  =1+1"
                            " \t+1", "' \t+1"
                            "  -1", "'  -1"
                            " \t@call", "' \t@call"
                            "\tordinary", "'\tordinary"
                            "\rordinary", "'\rordinary"
                            "  \rordinary", "'  \rordinary"
                            "'=1+1", "'=1+1"
                            "@call,\"quoted\"\r\nnext", "'@call,\"quoted\"\r\nnext"
                        ] do
                        let bytes =
                            CsvCell.format authored + ",sentinel\r\n" |> Encoding.UTF8.GetBytes

                        Expect.equal
                            (parseCsv bytes)
                            [ [ expected; "sentinel" ] ]
                            "Independent CSV readback")
            testCase "JSON and text preserve formula-leading authored values" (fun () ->
                let exported =
                    { row "=CASE" with
                        State =
                            CaseState.Active
                                { facts with
                                    ClaimantName = Inputs.name "=1+1"
                                    InsurerName = Inputs.name "@insurer"
                                }
                    }

                for options in [ []; [ "--format"; "json" ] ] do
                    let code, bytes, _, _ =
                        run options [ Outcome.Succeeded(page [ exported ] None) ]

                    let rendered = Encoding.UTF8.GetString bytes
                    Expect.equal code 0 "Exact-value output succeeds"

                    for authored in [ "=CASE"; "=1+1"; "@insurer" ] do
                        Expect.stringContains rendered authored "Authored spelling remains"

                        Expect.isFalse
                            (rendered.Contains("'" + authored))
                            "No CSV apostrophe is added")
            testCase
                "CSV is independently parseable with wire headers quoting and erased disclosure"
                (fun () ->
                    let erased =
                        { row "ERASED" with
                            Revision = CaseFixtures.revision 9L
                            State = CaseState.Erased
                        }

                    let code, bytes, errors, _ =
                        run
                            [ "--format"; "csv" ]
                            [ Outcome.Succeeded(page [ row "A,1"; erased ] None) ]

                    Expect.equal code 0 "CSV export succeeds"
                    Expect.isEmpty errors "Successful final-page CSV needs no error output"

                    Expect.isFalse
                        (bytes.Length >= 3 && bytes[0..2] = [| 0xEFuy; 0xBBuy; 0xBFuy |])
                        "UTF-8 has no BOM"

                    Expect.isTrue
                        (Encoding.UTF8.GetString(bytes).EndsWith("\r\n", StringComparison.Ordinal))
                        "Records use CRLF"

                    let parsed = parseCsv bytes

                    Expect.equal
                        parsed[0]
                        [
                            "caseReference"
                            "revision"
                            "state.kind"
                            "state.status"
                            "state.claimantName"
                            "state.insurerName"
                            "state.claimed.amount"
                            "state.claimed.currency"
                            "state.payable.amount"
                            "state.payable.currency"
                            "state.progress"
                            "changedAt"
                        ]
                        "The independently specified wire-path header"

                    Expect.equal
                        parsed[1]
                        [
                            "A,1"
                            "1"
                            "ACTIVE"
                            "OPENED"
                            "Synthetic, \"quoted\"\r\nclaimant"
                            "Synthetic insurer"
                            "1000"
                            "EUR"
                            ""
                            ""
                            "UNDECIDED"
                            "2026-10-28T12:30:00.000000Z"
                        ]
                        "Canonical values and authored punctuation survive"

                    Expect.equal
                        parsed[2]
                        [
                            "ERASED"
                            "9"
                            "ERASED"
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            "2026-10-28T12:30:00.000000Z"
                        ]
                        "Erased rows carry no business values")
            testCase "CSV all follows cursors with one header and unchanged filters" (fun () ->
                let code, bytes, _, calls =
                    run
                        [ "--format=csv"; "--all"; "--text"; "Synthetic"; "--order"; "recent" ]
                        [
                            Outcome.Succeeded(page [ row "FIRST" ] (Some "next-opaque"))
                            Outcome.Succeeded(page [ row "SECOND" ] None)
                        ]

                Expect.equal code 0 "Both pages succeed"
                Expect.equal (parseCsv bytes |> List.length) 3 "One header and two case rows"
                Expect.equal calls.Length 2 "One request per snapshot"

                let first =
                    Api.caseList.Input.Read "$" (Frames.input calls[0]) |> Fixtures.accepted

                let second =
                    Api.caseList.Input.Read "$" (Frames.input calls[1]) |> Fixtures.accepted

                Expect.equal first.Page.AfterCursor None "Start from the requested page"

                Expect.equal
                    second.Page.AfterCursor
                    (Some "next-opaque")
                    "Follow the returned opaque cursor"

                Expect.equal
                    second.Filter
                    first.Filter
                    "Page traversal does not change the admitted query")
            testCase
                "refusal stops CSV traversal and reports the reached cursor on stderr"
                (fun () ->
                    let code, bytes, errors, calls =
                        run
                            [ "--format"; "csv"; "--all" ]
                            [
                                Outcome.Succeeded(page [ row "FIRST" ] (Some "refused-cursor"))
                                Outcome.Rejected Refusal.InvalidCursor
                            ]

                    Expect.equal code 2 "The refusal controls exit status"
                    Expect.equal calls.Length 2 "Do not continue after refusal"

                    Expect.equal
                        (parseCsv bytes |> List.length)
                        2
                        "Output stays valid CSV through the last accepted page"

                    Expect.stringContains
                        (Encoding.UTF8.GetString errors)
                        "Stopped at cursor refused-cursor."
                        "The caller can identify the reached cursor")
            testCase "JSON all emits one structured frame per page" (fun () ->
                let code, bytes, _, calls =
                    run
                        [ "--format"; "json"; "--all" ]
                        [
                            Outcome.Succeeded(page [ row "FIRST" ] (Some "next"))
                            Outcome.Succeeded(page [] None)
                        ]

                Expect.equal code 0 "JSON traversal succeeds"
                Expect.equal calls.Length 2 "Two snapshots"

                let lines =
                    Encoding.UTF8
                        .GetString(bytes)
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)

                Expect.equal lines.Length 2 "One frame per page"

                for line in lines do
                    Expect.isOk
                        (Frames.decode (Encoding.UTF8.GetBytes line))
                        "Each line is a complete admitted frame")
            testCase "text all has one translated header" (fun () ->
                let code, bytes, _, _ =
                    run
                        [ "--all" ]
                        [
                            Outcome.Succeeded(page [ row "FIRST" ] (Some "next"))
                            Outcome.Succeeded(page [ row "SECOND" ] None)
                        ]

                Expect.equal code 0 "Text traversal succeeds"

                let lines =
                    Encoding.UTF8
                        .GetString(bytes)
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)

                Expect.equal
                    (lines
                     |> Array.filter (fun line ->
                         line.Contains("Last changed", StringComparison.Ordinal))
                     |> Array.length)
                    1
                    "One header across all pages"

                let rendered = Encoding.UTF8.GetString bytes
                Expect.stringContains rendered "FIRST" "First page is retained"
                Expect.stringContains rendered "SECOND" "Second page is retained")
            testCase "CSV and all options apply only to case lists" (fun () ->
                for args in
                    [
                        [ "case"; "get"; "x"; "--all" ]
                        [ "case"; "get"; "x"; "--format"; "csv" ]
                        [ "authority"; "list"; "--all" ]
                    ] do
                    Expect.isError
                        (Grammar.prepare (fun () -> "unused") args)
                        "Another command cannot silently acquire export or traversal semantics")
        ]
