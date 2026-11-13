module ClaimCore.Tests.CaseListCommandTests

open ClaimCore.Wire.Export

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

let private moment = DateTimeOffset(2026, 10, 28, 12, 30, 0, TimeSpan.Zero)

let private facts =
    {
        CaseSummaryFacts.Fields =
            Map.ofList
                [
                    BusinessField.ClaimantName,
                    Some(
                        CaseFieldValue.Business(
                            Admitted.Name(Inputs.name "Synthetic, \"quoted\"\r\nclaimant")
                        )
                    )
                    BusinessField.InsurerName,
                    Some(CaseFieldValue.Business(Admitted.Name(Inputs.name "Synthetic insurer")))
                    BusinessField.ClaimedAmount,
                    Some(
                        CaseFieldValue.Business(
                            Admitted.Amount(Amount.parse "1000.00" |> Fixtures.accepted)
                        )
                    )
                    BusinessField.ClaimedCurrency,
                    Some(
                        CaseFieldValue.Business(
                            Admitted.Currency(Currency.parse "EUR" |> Fixtures.accepted)
                        )
                    )
                    BusinessField.PayableAmount, None
                    BusinessField.PayableCurrency, None
                ]
    }

let private parties claimant insurer =
    { facts with
        Fields =
            facts.Fields
            |> Map.add
                BusinessField.ClaimantName
                (Some(CaseFieldValue.Business(Admitted.Name(Inputs.name claimant))))
            |> Map.add
                BusinessField.InsurerName
                (Some(CaseFieldValue.Business(Admitted.Name(Inputs.name insurer))))
    }

let private row reference =
    {
        Paid = None
        PaymentCount = 0L
        LastPaymentDate = None
        CaseId = CaseFixtures.caseId (System.Guid.Parse "20000000-0000-4000-8000-000000000001")
        Reference = Inputs.reference reference
        Revision = CaseFixtures.revision 1L
        WorkRevision = Revision.zero
        CanAssign = false
        ChangedAt = moment
        State = CaseState.Active facts
        Stages =
            Some
                {
                    Current = CaseStageKind.Opened
                    Completed = [ CaseStageKind.Opened ]
                }
        Headline = CaseHeadline.Undecided
        Attention = []
        NextAction = None
        Responsible = None
        PendingReminders = 0L
    }

let private page items next =
    {
        Basis = Inputs.workBasis
        Items = items
        Offers = []
        NextCursor = next
        ExactReference = None
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

                    CliReply.result
                        frame
                        (Api.caseList.Output.Write(
                            match Frames.representation frame with
                            | Representation.Human ->
                                HumanReplies.read HumanReadDocuments.cases response
                                |> RepresentationReply.Human
                            | Representation.Data -> RepresentationReply.Data response
                        ))
                    |> Fixtures.accepted
                    |> Task.FromResult
            Deliver =
                fun frame reply ->
                    let bytes =
                        if pending.Format = VerbFormat.Json then
                            reply.Bytes
                        else
                            ReplyText.bytesFor pending.Language (Some frame) reply.Frame

                    output.Write bytes
                    output.WriteByte(byte '\n')
            Output = fun bytes -> output.Write(bytes: byte array)
            Errors = fun bytes -> errors.Write(bytes: byte array)
        }

    let code =
        CaseListCommand.runWith
            { Width = 4096; BoldHeader = false }
            io
            pending
            CancellationToken.None
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
            testCase "case list labels composite headline as state" (fun () ->
                let entries = CaseListScreen.caseSummary (row "STATE-1") |> List.collect _.Entries
                let headline = entries |> List.find (fun entry -> entry.Key = "headline")

                Expect.equal
                    headline.LabelId
                    "label.caseState"
                    "A composite headline is not the status field"

                Expect.equal
                    headline.Value
                    (PresentedValue.State CaseHeadline.Undecided)
                    "The supplied headline retains its meaning"

                let output =
                    CaseListScreen.rows [ row "STATE-1" ]
                    |> ViewText.render "en"
                    |> String.concat "\n"

                Expect.stringContains
                    output
                    "Case state"
                    "The rendered column names the composite state"

                Expect.isFalse
                    (output.Contains "Case status")
                    "The headline is not labelled as operator status")
            testCase "responsible actor flag preserves the wire filter" (fun () ->
                let actor = System.Guid.Parse "10000000-0000-7000-8000-000000000071"

                let prepared =
                    Grammar.prepare
                        (fun () -> "10000000-0000-7000-8000-000000000072")
                        [ "case"; "list"; "--responsible-actor"; actor.ToString("D") ]
                    |> function
                        | Ok value -> value
                        | Error issues -> failtestf "%A" issues

                let input = Frames.value Api.caseList prepared.Invocation |> Option.get

                Expect.equal
                    (input.Filter.ResponsibleActor |> Option.map ActorId.value)
                    (Some actor)
                    "No client-side filtering")
            testCase "CSV exports paid amount entry count and last payment date" (fun () ->
                let original = row "PAY-CSV"
                let payable = Inputs.money "100" "EUR"

                let values =
                    facts.Fields
                    |> Map.add
                        BusinessField.PayableAmount
                        (Some(CaseFieldValue.Business(Admitted.Amount(Money.amount payable))))
                    |> Map.add
                        BusinessField.PayableCurrency
                        (Some(
                            CaseFieldValue.Business(Admitted.Currency(Money.currencyValue payable))
                        ))

                let exported =
                    { original with
                        Headline = CaseHeadline.PartlyPaid
                        Paid = Some(Inputs.money "60.25" "EUR")
                        PaymentCount = 2L
                        LastPaymentDate = Some(Inputs.date "2026-08-21")
                        State = CaseState.Active { facts with Fields = values }
                    }

                let code, bytes, _, _ =
                    run [ "--format"; "csv" ] [ Outcome.Succeeded(page [ exported ] None) ]

                Expect.equal code 0 "CSV succeeds"
                let rows = parseCsv bytes
                let columns = List.zip rows[0] rows[1] |> Map.ofList
                Expect.equal columns["paidAmount"] "60.25" "Exact canonical paid amount"
                Expect.equal columns["paymentCount"] "2" "Entry count"
                Expect.equal columns["lastPaymentDate"] "2026-08-21" "Canonical payment date"

                Expect.isFalse
                    (Map.containsKey "paymentDate" columns)
                    "There is no obsolete single-date column")
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
                                State = CaseState.Active(parties claimant insurer)
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
                            cells[6]
                            ("'" + claimant)
                            "Claimant protection precedes quoting"

                        Expect.equal
                            cells[7]
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
            testCase
                "descriptor actor names escape retained controls and bidi without changing JSON"
                (fun () ->
                    let raw = "Agent\u001B[31m\u202EB\\u{202E}"

                    let actor =
                        {
                            ActorId =
                                CaseFixtures.actorId (
                                    Guid.Parse "30000000-0000-4000-8000-000000000001"
                                )
                            DisplayName =
                                DisplayName.Named(PartyName.parse raw |> Fixtures.accepted)
                            PrincipalKind = PrincipalKind.Human
                            Availability = ActorAvailability.Available
                        }

                    let value =
                        { row "SAFE" with
                            Responsible = Some actor
                        }

                    Expect.stringContains
                        (TextDescriptorValue.render
                            "en"
                            (ClaimCore.Presentation.PresentedValue.Actor actor))
                        "Agent\\u{001B}[31m\\u{202E}B\\\\u{202E}"
                        "The actual typed formatter distinguishes controls from literal escapes before layout"

                    for options in [ []; [ "--all" ] ] do
                        let code, bytes, _, _ =
                            run options [ Outcome.Succeeded(page [ value ] None) ]

                        let rendered = Encoding.UTF8.GetString bytes
                        Expect.equal code 0 "The actual descriptor-backed CLI path succeeds"

                        Expect.stringContains
                            rendered
                            (ReplyTextFormat.label "en" "field.responsibleActor.label")
                            "The wide layout retains the actual actor column"

                        Expect.stringContains
                            rendered
                            "Agent"
                            "The actor value remains present even when its escaped tail is truncated"

                        Expect.isFalse
                            (rendered.Contains('\u001B') || rendered.Contains('\u202E'))
                            "Untrusted terminal controls and bidi never become layout"

                    let code, bytes, _, _ =
                        run [ "--format"; "json" ] [ Outcome.Succeeded(page [ value ] None) ]

                    Expect.equal code 0 "Structured output remains supported"

                    let decoded =
                        Frames.decode (
                            Encoding.UTF8.GetString(bytes).TrimEnd() |> Encoding.UTF8.GetBytes
                        )
                        |> Fixtures.accepted

                    match decoded.Result with
                    | FrameResult.Reply reply ->
                        match ServiceReply.value Api.caseList reply with
                        | Some(RepresentationReply.Data(Outcome.Succeeded page)) ->
                            Expect.equal
                                page.Items.Head.Responsible
                                (Some actor)
                                "JSON retains the exact typed actor value"
                        | other -> failtestf "Expected structured case page: %A" other
                    | _ -> failtest "Expected a structured reply")
            testCase "JSON and text preserve formula-leading authored values" (fun () ->
                let exported =
                    { row "=CASE" with
                        State = CaseState.Active(parties "=1+1" "@insurer")
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
                            CaseId =
                                CaseFixtures.caseId (
                                    Guid.Parse "20000000-0000-4000-8000-000000000002"
                                )
                            Revision = CaseFixtures.revision 9L
                            State = CaseState.Erased
                            Stages = None
                            Headline = CaseHeadline.Erased
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
                            "workRevision"
                            "state.kind"
                            "headline"
                            "attention"
                            "state.fields.claimantName"
                            "state.fields.insurerName"
                            "state.fields.claimedAmount"
                            "state.fields.claimedCurrency"
                            "state.fields.payableAmount"
                            "state.fields.payableCurrency"
                            "paidAmount"
                            "paymentCount"
                            "lastPaymentDate"
                            "responsible.actorId"
                            "responsible.displayName.value"
                            "responsible.displayName.kind"
                            "responsible.availability"
                            "nextAction.reminderId"
                            "nextAction.action"
                            "nextAction.due"
                            "nextAction.dueState"
                            "pendingReminders"
                            "changedAt"
                            "basis.businessDate"
                            "basis.timeZone"
                        ]
                        "The independently specified generated-fact and core-state headers"

                    Expect.equal
                        parsed[1]
                        [
                            "A,1"
                            "1"
                            "0"
                            "ACTIVE"
                            "UNDECIDED"
                            ""
                            "Synthetic, \"quoted\"\r\nclaimant"
                            "Synthetic insurer"
                            "1000"
                            "EUR"
                            ""
                            ""
                            ""
                            "0"
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            "0"
                            "2026-10-28T12:30:00.000000Z"
                            "2026-08-31"
                            "UTC"
                        ]
                        "Canonical values and authored punctuation survive"

                    Expect.equal
                        parsed[2]
                        [
                            "ERASED"
                            "9"
                            "0"
                            "ERASED"
                            "ERASED"
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            "0"
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            ""
                            "0"
                            "2026-10-28T12:30:00.000000Z"
                            "2026-08-31"
                            "UTC"
                        ]
                        "Erased rows carry no business values")
            testCase
                "CSV includes explicit work facts and their authoritative date basis"
                (fun () ->
                    let actor =
                        {
                            ActorId =
                                CaseFixtures.actorId (
                                    Guid.Parse "80000000-0000-4000-8000-000000000001"
                                )
                            DisplayName =
                                DisplayName.Named(
                                    PartyName.parse "=authored display name" |> Fixtures.accepted
                                )
                            PrincipalKind = PrincipalKind.Human
                            Availability = ActorAvailability.NotEligible
                        }

                    let item =
                        { row "WORK-CSV" with
                            Responsible = Some actor
                            NextAction =
                                Some
                                    {
                                        ReminderId =
                                            ReminderId.create (
                                                Guid.Parse "80000000-0000-4000-8000-000000000002"
                                            )
                                            |> Option.get
                                        Action =
                                            ReminderAction.parse "Review evidence"
                                            |> Fixtures.accepted
                                        Due = Inputs.date "2026-08-31"
                                        DueState = CaseDue.Today
                                    }
                            PendingReminders = 1L
                        }

                    let code, bytes, _, _ =
                        run [ "--format"; "csv" ] [ Outcome.Succeeded(page [ item ] None) ]

                    Expect.equal code 0 "The actual CLI exports a successful work page"
                    let parsed = parseCsv bytes
                    let cells = List.zip parsed[0] parsed[1] |> Map.ofList

                    for key, expected in
                        [
                            "responsible.actorId", "80000000-0000-4000-8000-000000000001"
                            "responsible.displayName.value", "'=authored display name"
                            "nextAction.due", "2026-08-31"
                            "nextAction.dueState", "TODAY"
                            "basis.businessDate", "2026-08-31"
                            "basis.timeZone", "UTC"
                        ] do
                        Expect.equal
                            (Map.find key cells)
                            expected
                            "Identity, canonical facts and date basis remain distinct"

                    Expect.stringContains
                        (Encoding.UTF8.GetString bytes)
                        "'=authored display name"
                        "New profile-name column retains formula neutrality")
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
                         line.Contains("Case facts changed", StringComparison.Ordinal))
                     |> Array.length)
                    1
                    "One header across all pages"

                let rendered = Encoding.UTF8.GetString bytes
                Expect.stringContains rendered "FIRST" "First page is retained"
                Expect.stringContains rendered "SECOND" "Second page is retained"

                Expect.equal
                    (lines
                     |> Array.filter (fun line ->
                         line.StartsWith("Observed ", StringComparison.Ordinal))
                     |> Array.length)
                    0
                    "Default text omits technical observation details"

                Expect.equal
                    (lines |> Array.filter ((=) "Shown: 1") |> Array.length)
                    2
                    "Each real page has its own count")
            testCase "only declared paged readers accept CSV and all options" (fun () ->
                for args in
                    [
                        [ "case"; "get"; "x"; "--all" ]
                        [ "case"; "get"; "x"; "--format"; "csv" ]
                        [ "people"; "list"; "--all" ]
                    ] do
                    Expect.isError
                        (Grammar.prepare (fun () -> "unused") args)
                        "Another command cannot silently acquire export or traversal semantics")
        ]
