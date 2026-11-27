module ClaimCore.Tests.PageCommandTests

open ClaimCore.Wire.Export

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Wire.Export
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

let private moment = DateTimeOffset(2026, 10, 28, 12, 30, 0, TimeSpan.Zero)

let private facts =
    {
        ClosureOutcome = None
        CaseSummaryFacts.Parties =
            Map.ofList
                [
                    PartyRole.Claimant, [ DomainInputs.name "Synthetic, \"quoted\"\r\nclaimant" ]
                    PartyRole.Insurer, [ DomainInputs.name "Synthetic insurer" ]
                ]
        Fields =
            Map.ofList
                [
                    BusinessField.ClaimedAmount,
                    Some(
                        CaseFieldValue.Business(
                            Admitted.Amount(Amount.parse "1000.00" |> CaseBuilders.accepted)
                        )
                    )
                    BusinessField.ClaimedCurrency,
                    Some(
                        CaseFieldValue.Business(
                            Admitted.Currency(Currency.parse "EUR" |> CaseBuilders.accepted)
                        )
                    )
                    BusinessField.PayableAmount, None
                    BusinessField.PayableCurrency, None
                ]
    }

let private parties claimant insurer =
    { facts with
        Parties =
            Map.ofList
                [
                    PartyRole.Claimant, [ DomainInputs.name claimant ]
                    PartyRole.Insurer, [ DomainInputs.name insurer ]
                ]
    }

let private row reference =
    {
        NetPaid = None
        PaymentCount = 0L
        LastPaymentDate = None
        CaseId = CaseFixtures.caseId (System.Guid.Parse "20000000-0000-4000-8000-000000000001")
        Reference = DomainInputs.reference reference
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
        Basis = DomainInputs.workBasis
        Items = items
        Offers = []
        NextCursor = next
        AuthorizedScopeEmpty = false
        ExactReference = None
    }

let private prepared arguments =
    Grammar.prepare (fun () -> "unused") ([ "case"; "list" ] @ arguments)
    |> CaseBuilders.accepted

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
                                HumanReplies.read CaseListScreen.document response
                                |> RepresentationReply.Human
                            | Representation.Data -> RepresentationReply.Data response
                        ))
                    |> CaseBuilders.accepted
                    |> Task.FromResult
            Deliver =
                fun frame reply ->
                    let bytes =
                        if pending.Format = VerbFormat.Json then
                            reply.Bytes
                        else
                            ReplyText.bytesFor pending.Rendering (Some frame) reply.Frame

                    output.Write bytes
                    output.WriteByte(byte '\n')
            Output = fun bytes -> output.Write(bytes: byte array)
            Errors = fun bytes -> errors.Write(bytes: byte array)
        }

    let code =
        PageCommand.runWith { Width = 4096; BoldHeader = false } io pending CancellationToken.None
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

let private declaredPages () =
    let replace path value document =
        let rec update path document =
            match path, document with
            | [], _ -> value
            | name :: rest, JsonValue.Object fields when
                fields |> List.exists (fun (key, _) -> key = name)
                ->
                JsonValue.Object(
                    fields
                    |> List.map (fun (key, child) ->
                        key, if key = name then update rest child else child)
                )
            | _ -> failtest "A paged response fixture requires the declared continuation path."

        update path document

    for declaration in EndpointDeclarations.queries |> List.filter _.Paged do
        let entry =
            CommandGrammar.operations |> List.find (fun entry -> entry = declaration.Id)

        let operands =
            CommandGrammar.inputs entry
            |> List.choose (fun input ->
                if input.Argument <> CommandArgument.Operand then
                    None
                else
                    match input.Name with
                    | TaskInputId.Target(InputTarget.Field BusinessField.CaseReference) ->
                        Some "CC-FD0Q-1HG1-NBRR-1GD8-83"
                    | TaskInputId.ActorId -> Some "10000000-0000-4000-8000-000000000099"
                    | _ -> failtest "The paging fixture needs an independently supplied operand.")

        let prepared =
            Grammar.prepare
                (fun () -> failtest "A read never allocates an operation ID")
                (CommandGrammar.words entry @ operands @ [ "--all"; "--format"; "json" ])
            |> Result.defaultWith (fun errors -> failtestf "%A" errors)

        let example =
            ResponseExamples.replies ()
            |> List.tryPick (fun output ->
                output.Accept
                    { new IEndpointOutputVisitor<JsonValue option> with
                        member _.Visit<'input, 'output>
                            (endpoint: Endpoint<'input, 'output>, value)
                            =
                            let wire = endpoint.Output.Write value

                            if
                                endpoint.Token = declaration.Id
                                && endpoint.Classify value = ReplyKind.Succeeded
                                && JsonValue.tryMember "representation" wire =
                                    Some(JsonValue.String "DATA")
                            then
                                Some wire
                            else
                                None
                    })
            |> Option.defaultWith (fun () ->
                failtest ("A typed DATA fixture is required: " + declaration.Id))

        let calls = ResizeArray<JsonValue>()

        let io: ReadPageIO =
            {
                Exchange =
                    fun frame _ ->
                        calls.Add(Frames.input frame)

                        let cursor =
                            if calls.Count = 1 then
                                JsonValue.String "second-page"
                            else
                                JsonValue.Null

                        let reply = replace [ "outcome"; "data"; "nextCursor" ] cursor example
                        CliReply.result frame reply |> CaseBuilders.accepted |> Task.FromResult
                Deliver = fun _ _ -> ()
                Output = ignore
                Errors = ignore
            }

        let code =
            PageCommand.run io prepared CancellationToken.None
            |> fun pending -> pending.GetAwaiter().GetResult()

        Expect.equal code 0 "Every declared page uses the common traversal"
        Expect.equal calls.Count 2 "The returned continuation is followed exactly once"

        Expect.equal
            (JsonValue.tryMember "cursor" calls[1])
            (Some(JsonValue.String "second-page"))
            "The cursor reaches the next typed request"

        let filters =
            function
            | JsonValue.Object fields -> fields |> List.filter (fun (key, _) -> key <> "cursor")
            | _ -> failtest "An object request is required"

        Expect.equal
            (filters calls[1])
            (filters calls[0])
            "Traversal preserves every other request member"

[<Tests>]
let accessRequestRestart =
    testCase
        "native access-request traversal refuses a moved inbox and a fresh command starts at page one"
        (fun () ->
            let prepared =
                Grammar.prepare
                    (fun () -> failtest "Read allocated an operation")
                    [ "people"; "requests"; "--all"; "--format"; "json" ]
                |> CaseBuilders.accepted

            let requester =
                PrincipalKey.human "https://issuer.example/" "waiting" |> CaseBuilders.accepted

            let first =
                {
                    AccessRequestPage.Basis = DomainInputs.workBasis
                    Items =
                        [
                            {
                                Principal = requester
                                FirstSeen = moment
                                LastSeen = moment
                                Claims = RequesterClaims.empty
                            }
                        ]
                    NextCursor = Some "pending-inbox-page"
                }

            let run (replies: Outcome<AccessRequestPage> list) =
                let frames = ResizeArray<Invocation>()
                let queue = Collections.Generic.Queue<Outcome<AccessRequestPage>>(replies)
                use errors = new MemoryStream()

                let io =
                    {
                        Exchange =
                            fun frame _ ->
                                frames.Add frame

                                CliReply.result
                                    frame
                                    (Api.peopleRequests.Output.Write(
                                        RepresentationReply.Data(queue.Dequeue())
                                    ))
                                |> CaseBuilders.accepted
                                |> Task.FromResult
                        Deliver = fun _ _ -> ()
                        Output = ignore
                        Errors = fun bytes -> errors.Write bytes
                    }

                let code =
                    PageCommand.run io prepared CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let cursors =
                    frames |> Seq.map (Frames.input >> JsonValue.tryMember "cursor") |> Seq.toList

                code, cursors, Encoding.UTF8.GetString(errors.ToArray())

            let status, requests, stderr =
                run [ Outcome.Succeeded first; Outcome.Rejected Refusal.TraversalChanged ]

            Expect.notEqual status 0 "A partial traversal exits nonzero"

            Expect.equal
                requests
                [ None; Some(JsonValue.String "pending-inbox-page") ]
                "Refusal stops paging"

            Expect.stringContains
                stderr
                "Stopped at cursor pending-inbox-page."
                "Stable continuation diagnostic"

            let status, requests, _ = run [ Outcome.Succeeded { first with NextCursor = None } ]
            Expect.equal status 0 "A fresh command succeeds"
            Expect.equal requests [ None ] "The fresh command requests page one")

[<Tests>]
let tests =
    testList
        "paged export and traversal"
        [
            testCase
                "all declared paged queries traverse their actual typed reply grammar"
                declaredPages
            testCase "case list labels composite headline as state" (fun () ->
                let entries = CaseListScreen.caseSummary (row "STATE-1") |> List.collect _.Entries
                let headline = entries |> List.find (fun entry -> entry.Key = "headline")

                Expect.equal
                    headline.LabelId
                    "label.caseState"
                    "A composite headline is not the status field"

                Expect.equal
                    headline.Value
                    (PresentedValue.State(CaseHeadline.Undecided, None))
                    "The supplied headline retains its meaning"

                let output =
                    CaseListScreen.rows [ row "STATE-1" ]
                    |> ViewText.render (RenderingContext.forLanguage "en")
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
                let payable = DomainInputs.money "100" "EUR"

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
                        NetPaid =
                            Some
                                {
                                    Payable = DomainInputs.money "100" "EUR"
                                    NetPaid =
                                        DecimalTotal.parse "60.25" |> Result.toOption |> Option.get
                                }
                        PaymentCount = 2L
                        LastPaymentDate = Some(DomainInputs.date "2026-08-21")
                        State = CaseState.Active { facts with Fields = values }
                    }

                let bytes =
                    Array.append
                        CaseCsv.csvHeader
                        (CaseCsv.csvRows DomainInputs.workBasis [ exported ])

                let rows = parseCsv bytes
                let columns = List.zip rows[0] rows[1] |> Map.ofList
                Expect.equal columns["netPaid"] "60.25" "Exact canonical paid amount"
                Expect.equal columns["paymentCount"] "2" "Entry count"
                Expect.equal columns["lastPaymentDate"] "2026-08-21" "Canonical payment date"

                Expect.isFalse
                    (Map.containsKey "paymentDate" columns)
                    "There is no obsolete single-date column")
            testCase
                "CSV protects formula-leading claimant and insurer values and preserves case references"
                (fun () ->
                    for prefix in [ "="; "+"; "-"; "@"; "\t"; "\r" ] do
                        let reference =
                            if prefix = "\t" || prefix = "\r" then
                                "CC-51N1-TT7K-ZWRB-KAF6-14"
                            else
                                "CC-0123-4567-89AB-CDEF-41"

                        let claimant = prefix + "call,\"quoted\"\r\nclaimant"
                        let insurer = prefix + "insurer"

                        let exported =
                            { row reference with
                                State = CaseState.Active(parties claimant insurer)
                            }

                        let bytes =
                            Array.append
                                CaseCsv.csvHeader
                                (CaseCsv.csvRows DomainInputs.workBasis [ exported ])

                        let cells = (parseCsv bytes)[1]

                        Expect.equal cells[0] reference "Issued case references are preserved"

                        Expect.equal
                            cells[6]
                            ("'" + claimant)
                            "Claimant protection precedes quoting"

                        Expect.equal
                            cells[7]
                            ("'" + insurer)
                            "Insurer protection uses the same rule")
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
                                DisplayName.Named(PartyName.parse raw |> CaseBuilders.accepted)
                            PrincipalKind = PrincipalKind.Human
                            Availability = ActorAvailability.Available
                        }

                    let value =
                        { row "SAFE" with
                            Responsible = Some actor
                        }

                    Expect.stringContains
                        (TextDescriptorValue.render
                            (RenderingContext.forLanguage "en")
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
                            (ReplyTextFormat.label
                                (RenderingContext.forLanguage "en")
                                "field.responsibleActor.label")
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
                        |> CaseBuilders.accepted

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
                    { row "CC-51N1-TT7K-ZWRB-KAF6-14" with
                        State = CaseState.Active(parties "=1+1" "@insurer")
                    }

                for options in [ []; [ "--format"; "json" ] ] do
                    let code, bytes, _, _ =
                        run options [ Outcome.Succeeded(page [ exported ] None) ]

                    let rendered = Encoding.UTF8.GetString bytes
                    Expect.equal code 0 "Exact-value output succeeds"

                    for authored in [ "=1+1"; "@insurer" ] do
                        Expect.stringContains rendered authored "Authored spelling remains"

                        Expect.isFalse
                            (rendered.Contains("'" + authored))
                            "No CSV apostrophe is added")
            testCase
                "CSV is independently parseable with wire headers quoting and erased disclosure"
                (fun () ->
                    let erased =
                        { row "CC-Z399-4K58-1T11-N5NF-88" with
                            CaseId =
                                CaseFixtures.caseId (
                                    Guid.Parse "20000000-0000-4000-8000-000000000002"
                                )
                            Revision = CaseFixtures.revision 9L
                            State = CaseState.Erased
                            Stages = None
                            Headline = CaseHeadline.Erased
                        }

                    let bytes =
                        Array.append
                            CaseCsv.csvHeader
                            (CaseCsv.csvRows
                                DomainInputs.workBasis
                                [ row "CC-QH77-QTC2-NDWY-RKXP-15"; erased ])

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
                            "claimants"
                            "insurers"
                            "state.fields.claimedAmount"
                            "state.fields.claimedCurrency"
                            "state.fields.payableAmount"
                            "state.fields.payableCurrency"
                            "netPaid"
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
                            "CC-QH77-QTC2-NDWY-RKXP-15"
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
                            "CC-Z399-4K58-1T11-N5NF-88"
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
                                    PartyName.parse "=authored display name"
                                    |> CaseBuilders.accepted
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
                                            |> CaseBuilders.accepted
                                        Due = DomainInputs.date "2026-08-31"
                                        DueState = CaseDue.Today
                                    }
                            PendingReminders = 1L
                        }

                    let bytes =
                        Array.append
                            CaseCsv.csvHeader
                            (CaseCsv.csvRows DomainInputs.workBasis [ item ])

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
            testCase "JSON all emits one structured frame per page" (fun () ->
                let code, bytes, _, calls =
                    run
                        [ "--format"; "json"; "--all" ]
                        [
                            Outcome.Succeeded(
                                page [ row "CC-4SYK-Q0D9-VKCK-FWXM-34" ] (Some "next")
                            )
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
                            Outcome.Succeeded(
                                page [ row "CC-4SYK-Q0D9-VKCK-FWXM-34" ] (Some "next")
                            )
                            Outcome.Succeeded(page [ row "CC-GHT7-VKC3-3HRK-283T-94" ] None)
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
                Expect.stringContains rendered "CC-4SYK-Q0D9-VKCK-FWXM-34" "First page is retained"

                Expect.stringContains
                    rendered
                    "CC-GHT7-VKC3-3HRK-283T-94"
                    "Second page is retained"

                Expect.equal
                    (lines
                     |> Array.filter (fun line ->
                         line.StartsWith("Business date: ", StringComparison.Ordinal)
                         && line.Contains(" · Observed at: ", StringComparison.Ordinal))
                     |> Array.length)
                    2
                    "Each page has one observation footer from its basis")
            testCase "only declared paged readers accept CSV and all options" (fun () ->
                for args in
                    [
                        [ "case"; "get"; "x"; "--all" ]
                        [ "case"; "get"; "x"; "--format"; "csv" ]
                        [ "people"; "list"; "--format"; "csv" ]
                    ] do
                    Expect.isError
                        (Grammar.prepare (fun () -> "unused") args)
                        "Another command cannot silently acquire export or traversal semantics")
        ]

[<Tests>]
let localeInvariance =
    testCase "DATA pagination bytes ignore independent display locale" (fun () ->
        let replies = [ Outcome.Succeeded(page [ row "CC-JNBE-ED50-FGSC-P1TC-23" ] None) ]

        for format in [ "json" ] do
            let code, original, _, _ =
                run [ "--format"; format; "--display-locale"; "en-GB" ] replies

            Expect.equal code 0 "Positive traversal"

            for locale in [ "lv-LV"; "de-DE"; "ar-EG" ] do
                let code, bytes, _, _ =
                    run [ "--format"; format; "--display-locale"; locale ] replies

                Expect.equal code 0 "Same traversal"
                Expect.equal bytes original "Exchange formats stay canonical")
