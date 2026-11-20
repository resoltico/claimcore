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
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

let private moment = DateTimeOffset(2026, 10, 28, 12, 30, 0, TimeSpan.Zero)

let private facts =
    {
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
                                HumanReplies.read HumanReadDocuments.cases response
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

    let declaration = EndpointDeclarations.find "case.list" |> Option.get

    let code =
        ReadCommand.runWith
            { Width = 4096; BoldHeader = false }
            declaration
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
                        Some "PAGE-FIXTURE"
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

let private snapshot success wrongRoute serialize =
    let mutable serializations = 0
    // Keep the test endpoint local. Its genuinely nonpaged grammar has no continuation member.
    let endpoint =
        { Api.caseGet with
            Csv =
                Some
                    {
                        Header = Csv.row [ "revision" ]
                        Rows =
                            fun _ value ->
                                serializations <- serializations + 1

                                if not serialize then
                                    invalidOp "Synthetic CSV serialization failure."

                                match value with
                                | RepresentationReply.Data(Outcome.Succeeded snapshot) ->
                                    Some(Csv.row [ Revision.format snapshot.Snapshot.Revision ])
                                | _ ->
                                    failtest "CSV serialization is only called for successful DATA."
                    }
        }

    let request =
        {
            CaseGetRequest.Target = CaseSelector.Reference "CSV-SNAPSHOT"
        }

    let frame = Frames.create endpoint request |> CaseBuilders.accepted

    let initial =
        Grammar.prepare
            (fun () -> failtest "A read never allocates an operation ID")
            [ "case"; "get"; "CSV-SNAPSHOT" ]
        |> CaseBuilders.accepted

    let prepared =
        { initial with
            Invocation = frame
            Format = VerbFormat.Csv
        }

    let endpointDeclaration = EndpointDeclarations.find "case.get" |> Option.get
    let declaration = { endpointDeclaration with Csv = true }

    let output = ResizeArray<byte array>()
    let errors = ResizeArray<byte array>()
    let mutable exchanges = 0

    let io: ReadPageIO =
        {
            Exchange =
                fun frame _ ->
                    exchanges <- exchanges + 1

                    Expect.equal
                        (Frames.representation frame)
                        Representation.Data
                        "The one exchange requests DATA"

                    Expect.isNone
                        (JsonValue.tryMember "cursor" (Frames.input frame))
                        "The snapshot has no cursor"

                    let correlated =
                        if wrongRoute then
                            Frames.create { endpoint with Id = Api.caseNotes.Id } request
                            |> CaseBuilders.accepted
                        else
                            frame

                    let value =
                        if success then
                            Outcome.Succeeded ResponseExamples.caseRead
                        else
                            Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)

                    CliReply.result
                        correlated
                        (endpoint.Output.Write(RepresentationReply.Data value))
                    |> CaseBuilders.accepted
                    |> Task.FromResult
            Deliver = fun _ _ -> failtest "CSV does not print a native reply frame"
            Output = output.Add
            Errors = errors.Add
        }

    let options = { Width = 4096; BoldHeader = false }

    let code =
        (ReadCommand.runWith options declaration io prepared CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    Expect.equal exchanges 1 "The snapshot uses exactly one exchange"
    code, List.ofSeq output, List.ofSeq errors, serializations

let private mismatchCsv () =
    for arguments, paged in
        [
            [ "case"; "list"; "--format"; "csv" ], true
            [ "case"; "get"; "CSV-SNAPSHOT" ], false
        ] do
        let initial =
            Grammar.prepare (fun () -> failtest "Read must not allocate an ID") arguments
            |> CaseBuilders.accepted

        let prepared = { initial with Format = VerbFormat.Csv }

        let declaration =
            EndpointDeclarations.find (Frames.endpoint prepared.Invocation).Token
            |> Option.get

        let declaredCsv = { declaration with Csv = true }
        let expectedHost = String.replicate 64 "a"
        use errors = new MemoryStream()
        let mutable count = 0

        let io =
            {
                Exchange =
                    fun frame _ ->
                        count <- count + 1

                        let reply =
                            CliReply.serviceFailure
                                frame
                                {
                                    Code = HostFailureCode.ContractMismatch
                                    Phase = Some ExecutionPhase.NotStarted
                                }

                        let identified = CliReply.withHostContractIdentity (Some expectedHost) reply

                        Expect.equal
                            identified.Bytes
                            reply.Bytes
                            "Local response metadata never changes machine JSON"

                        Task.FromResult identified
                Deliver = fun _ _ -> failtest "CSV failure must not print a machine frame"
                Output = ignore
                Errors = fun bytes -> errors.Write bytes
            }

        let code =
            ReadCommand.runWith
                { Width = 100; BoldHeader = false }
                declaredCsv
                io
                prepared
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Expect.equal count 1 "A mismatch cannot cause a repeat request"
        Expect.equal code 3 "The typed host mismatch preserves its exit"
        Expect.equal declaration.Paged paged "Each acquisition branch is exercised"
        let output = Encoding.UTF8.GetString(errors.ToArray())
        Expect.stringContains output expectedHost "CSV stderr names the admitted host contract"
        Expect.stringContains output ContractIdentity.value "CSV stderr names the local contract"

[<Tests>]
let snapshotCsvTests =
    testList
        "native CSV snapshot acquisition"
        [
            testCase
                "paged and snapshot CSV failures keep typed host identity on stderr"
                mismatchCsv
            testCase "one nonpaged DATA response serializes the complete CSV" (fun () ->
                let code, output, errors, calls = snapshot true false true
                Expect.equal code 0 "The admitted snapshot succeeded"

                Expect.equal
                    (output |> List.map Encoding.UTF8.GetString)
                    [ "revision\r\n1\r\n" ]
                    "Header and rows are emitted together with canonical line endings"

                Expect.isEmpty errors "No cursor or failure guidance on a snapshot success"
                Expect.equal calls 1 "The endpoint serializer runs once")
            testCase
                "a refusal, wrong endpoint or failed serializer publishes no header or rows"
                (fun () ->
                    for success, wrongRoute, serialize, calls in
                        [ false, false, true, 0; true, true, true, 0; true, false, false, 1 ] do
                        let code, output, errors, actual = snapshot success wrongRoute serialize
                        Expect.equal code 2 "The refusal or invalid response exits nonzero"
                        Expect.isEmpty output "Snapshot validation precedes any stdout bytes"
                        Expect.isNonEmpty errors "Failure guidance goes to stderr"

                        Expect.equal
                            actual
                            calls
                            "Only a correlated success reaches the serializer")
        ]

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
                    (PresentedValue.State CaseHeadline.Undecided)
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

                let code, bytes, _, _ =
                    run [ "--format"; "csv" ] [ Outcome.Succeeded(page [ exported ] None) ]

                Expect.equal code 0 "CSV succeeds"
                let rows = parseCsv bytes
                let columns = List.zip rows[0] rows[1] |> Map.ofList
                Expect.equal columns["netPaid"] "60.25" "Exact canonical paid amount"
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
            testCase "ordinary CSV reports the next cursor without following it" (fun () ->
                let code, bytes, errors, calls =
                    run
                        [ "--format"; "csv" ]
                        [ Outcome.Succeeded(page [ row "FIRST" ] (Some "next-opaque")) ]

                Expect.equal code 0 "One healthy page succeeds"
                Expect.equal calls.Length 1 "Only --all advances"
                Expect.hasLength (parseCsv bytes) 2 "One header and one row"

                Expect.stringContains
                    (Encoding.UTF8.GetString errors)
                    "next-opaque"
                    "Continuation guidance stays on stderr")
            testCase "CSV traversal refuses a repeated cursor" (fun () ->
                Expect.throwsT<InvalidOperationException>
                    (fun () ->
                        run
                            [ "--format"; "csv"; "--all" ]
                            [
                                Outcome.Succeeded(page [ row "FIRST" ] (Some "same"))
                                Outcome.Succeeded(page [ row "SECOND" ] (Some "same"))
                            ]
                        |> ignore)
                    "A repeated continuation cannot masquerade as end-of-data")
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
                    Api.caseList.Input.Read "$" (Frames.input calls[0]) |> CaseBuilders.accepted

                let second =
                    Api.caseList.Input.Read "$" (Frames.input calls[1]) |> CaseBuilders.accepted

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
                        [ "people"; "list"; "--format"; "csv" ]
                    ] do
                    Expect.isError
                        (Grammar.prepare (fun () -> "unused") args)
                        "Another command cannot silently acquire export or traversal semantics")
        ]

[<Tests>]
let localeInvariance =
    testCase "CSV and DATA pagination bytes ignore independent display locale" (fun () ->
        let replies = [ Outcome.Succeeded(page [ row "LOCALE" ] None) ]

        for format in [ "csv"; "json" ] do
            let code, original, _, _ =
                run [ "--format"; format; "--display-locale"; "en-GB" ] replies

            Expect.equal code 0 "Positive traversal"

            for locale in [ "lv-LV"; "de-DE"; "ar-EG" ] do
                let code, bytes, _, _ =
                    run [ "--format"; format; "--display-locale"; locale ] replies

                Expect.equal code 0 "Same traversal"
                Expect.equal bytes original "Exchange formats stay canonical")
