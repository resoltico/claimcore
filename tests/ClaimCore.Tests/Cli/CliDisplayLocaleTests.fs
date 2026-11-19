module ClaimCore.Tests.CliDisplayLocaleTests

open System
open System.Globalization
open System.IO
open System.Text
open Expecto
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open System.Threading.Tasks
open ClaimCore.TestSupport

let private required value =
    value |> Result.defaultWith (fun error -> failtestf "%A" error)

let private context language locale =
    RenderingContext.create language locale |> Option.get

let private read values name = Map.tryFind name (Map.ofList values)

[<Tests>]
let tests =
    testList
        "CLI independent display locale"
        [
            testCase
                "flag environment profile and language default have independent precedence"
                (fun () ->
                    let profile =
                        Map.ofList [ CliSetting.Language, "ar"; CliSetting.DisplayLocale, "de-DE" ]

                    let choose environment supplied =
                        CliLanguage.displayLocale (read environment) profile "en" supplied
                        |> required

                    Expect.equal
                        ((choose [ "CLAIMCORE_DISPLAY_LOCALE", "lv-LV" ] (Some "en-US"))
                         |> fun (value, source) -> value.DisplayLocale, source)
                        ("en-US", "flag")
                        "Flag wins"

                    Expect.equal
                        ((choose [ "CLAIMCORE_DISPLAY_LOCALE", "lv-LV" ] None)
                         |> fun (value, source) -> value.DisplayLocale, source)
                        ("lv-LV", "environment")
                        "Environment wins"

                    Expect.equal
                        ((choose [] None) |> fun (value, source) -> value.DisplayLocale, source)
                        ("de-DE", "profile")
                        "Profile wins"

                    Expect.equal
                        (CliLanguage.displayLocale (read []) Map.empty "ar" None
                         |> required
                         |> fun (value, source) -> value.Language, value.DisplayLocale, source)
                        ("ar", "ar-AE", "language")
                        "Declared language fallback"

                    for supplied in [ ""; "en"; "de-de"; "unsupported" ] do
                        Expect.isError
                            (CliLanguage.displayLocale (read []) Map.empty "en" (Some supplied))
                            "Invalid explicit values never fall back"

                    Expect.isError
                        (CliLanguage.displayLocale
                            (read [ "CLAIMCORE_DISPLAY_LOCALE", "unsupported" ])
                            profile
                            "en"
                            None)
                        "Invalid environment never falls back"

                    Expect.isError
                        (Grammar.prepare
                            (fun () -> "unused")
                            [ "case"; "get"; "LOCALE"; "--display-locale"; "unsupported" ])
                        "Invalid per-command locale is visible"

                    Expect.isError
                        (CliInvocations.admitConfiguredWith
                            (read [])
                            [ "case"; "get"; "LOCALE"; "--display-locale" ])
                        "Missing locale is not a fallback request")
            testCase
                "configured executable and shell boundary carries one resolved context into verbs and recovery"
                (fun () ->
                    let directory = Directory.CreateTempSubdirectory("claimcore-display-profile-")

                    try
                        let environment =
                            read
                                [
                                    "XDG_CONFIG_HOME", directory.FullName
                                    "CLAIMCORE_LANGUAGE", "en"
                                    "CLAIMCORE_DISPLAY_LOCALE", "lv-LV"
                                ]

                        let profile = CliProfile.location environment |> required

                        CliProfile.write
                            profile
                            (Map.ofList
                                [ CliSetting.Language, "ar"; CliSetting.DisplayLocale, "de-DE" ])
                        |> required

                        use profileDocument =
                            System.Text.Json.JsonDocument.Parse(File.ReadAllBytes profile)

                        Expect.equal
                            (profileDocument.RootElement.GetProperty("displayLocale").GetString())
                            "de-DE"
                            "Canonical stored property"

                        let profileOnly = read [ "XDG_CONFIG_HOME", directory.FullName ]

                        match
                            CliInvocations.admitConfiguredWith
                                profileOnly
                                [ "case"; "get"; "LOCALE" ]
                            |> required
                        with
                        | CliInvocation.Verb arguments ->
                            let prepared =
                                Grammar.prepare (fun () -> "unused") arguments |> required

                            Expect.equal
                                prepared.Rendering
                                (context "ar" "de-DE")
                                "Stored display locale is used independently from stored language"
                        | _ -> failtest "Human verb required"

                        let shown = CliConfigure.showWith environment |> required
                        use shownDocument = System.Text.Json.JsonDocument.Parse(shown: byte array)

                        let display =
                            shownDocument.RootElement.GetProperty("effectiveDisplayLocale")

                        Expect.equal
                            (display.GetProperty("value").GetString())
                            "lv-LV"
                            "Configure reports selected display locale"

                        Expect.equal
                            (display.GetProperty("source").GetString())
                            "environment"
                            "Configure reports its source"

                        Expect.isError
                            (RemoteConfiguration.validatePublic (
                                Map.ofList [ CliSetting.DisplayLocale, "unsupported" ]
                            ))
                            "Invalid profile locale is visible"

                        Expect.isOk
                            (RemoteConfiguration.validatePublic (
                                Map.ofList [ CliSetting.DisplayLocale, "de-DE" ]
                            ))
                            "Display preference can be configured before a connection"

                        let admitted =
                            CliInvocations.admitConfiguredWith
                                environment
                                [ "case"; "get"; "LOCALE" ]
                            |> required

                        match admitted with
                        | CliInvocation.Verb arguments ->
                            let prepared =
                                Grammar.prepare (fun () -> "unused") arguments |> required

                            Expect.equal
                                prepared.Rendering
                                (context "en" "lv-LV")
                                "Resolved context survives preparation"
                        | _ -> failtest "Human verb required"

                        match
                            CliInvocations.admitConfiguredWith
                                environment
                                [
                                    "recovery"
                                    "list"
                                    "--language"
                                    "ar"
                                    "--display-locale"
                                    "en-GB"
                                ]
                            |> required
                        with
                        | CliInvocation.Recovery selected ->
                            Expect.equal
                                selected.Rendering
                                (context "ar" "en-GB")
                                "Recovery resolves wording and display independently"
                        | _ -> failtest "Recovery required"

                        Expect.isError
                            (CliInvocations.admitConfiguredWith
                                (read
                                    [
                                        "XDG_CONFIG_HOME", directory.FullName
                                        "CLAIMCORE_DISPLAY_LOCALE", "bad"
                                    ])
                                [ "case"; "list"; "--dry-run" ])
                            "Invalid environment is visible offline"

                        File.WriteAllText(
                            profile,
                            """{"language":"en","displayLocale":"unsupported"}"""
                        )

                        Expect.isError
                            (CliProfile.read profile)
                            "Malformed stored locale does not become a default"

                        Expect.isError
                            (CliInvocations.admitConfiguredWith
                                environment
                                [ "case"; "get"; "LOCALE" ])
                            "Invalid loaded configuration refuses visibly"
                    finally
                        directory.Delete(true))
            testCase
                "machine discovery and configure admission do not resolve unused rendering preferences"
                (fun () ->
                    let read _ =
                        failtest "No preference IO belongs to this admission path"

                    for arguments in
                        [
                            [ "--version" ]
                            [ "schema"; "invocation" ]
                            [ "configure"; "--show" ]
                            [ "shell" ]
                        ] do
                        CliInvocations.admitConfiguredWith read arguments |> required |> ignore)
            testCase "regional formatting does not change wording or ambient culture" (fun () ->
                let before = CultureInfo.CurrentCulture
                let date = DomainInputs.date "2026-11-04"

                for locale, money, dateText, count in
                    [
                        "lv-LV", "1\u00a0234,50 EUR", "4 nov. 2026", "12\u00a0345"
                        "de-DE", "1.234,50 EUR", "4. Nov. 2026", "12.345"
                        "en-US", "1,234.50 EUR", "Nov 4, 2026", "12,345"
                    ] do
                    let rendering = context "en" locale

                    Expect.equal
                        (ReplyTextFormat.money rendering (DomainInputs.money "1234.5" "EUR"))
                        money
                        "Exact regional money"

                    Expect.equal
                        (ReplyTextFormat.date rendering date)
                        dateText
                        "Regional calendar order and wording"

                    Expect.equal
                        (ReplyTextFormat.number rendering 12345L)
                        count
                        "Regional count grouping"

                    Expect.equal
                        (ReplyTextFormat.label rendering "label.payments")
                        "Payments"
                        "English wording stays English"

                let egypt = context "en" "ar-EG"

                Expect.equal
                    (ReplyTextFormat.number egypt 12345L)
                    "١٢٬٣٤٥"
                    "Locale native digits apply to formatted counts"

                Expect.equal
                    (ReplyTextFormat.money egypt (DomainInputs.money "1234.5" "EUR"))
                    "١٬٢٣٤٫٥٠ EUR"
                    "Native digits preserve exact fractions"

                Expect.equal
                    (ReplyTextFormat.date egypt date)
                    "٤ نوفمبر ٢٠٢٦"
                    "Locale calendar digits"

                let arabic = context "ar" "en-GB"

                Expect.equal
                    (ReplyTextFormat.date arabic date)
                    "4 Nov 2026"
                    "English date with Arabic messages"

                Expect.equal
                    (ReplyTextFormat.money arabic (DomainInputs.money "1234.5" "EUR"))
                    "1,234.50 EUR"
                    "English money with Arabic messages"

                Expect.equal
                    (ReplyTextFormat.label arabic "label.payments")
                    (MessageCatalogs.render "ar" "label.payments" Map.empty)
                    "Shared Arabic wording"

                Expect.isTrue
                    (obj.ReferenceEquals(before, CultureInfo.CurrentCulture))
                    "No ambient culture mutation")
            testCase
                "actual shell commands carry independent rendering without changing query bytes"
                (fun () ->
                    let document =
                        {
                            HumanDocument.Subject = DocumentSubject.List
                            AttachmentObservation = None
                            Screen =
                                Screen.create
                                    [
                                        ScreenEntries.entryRegion
                                            RegionRole.Facts
                                            None
                                            [
                                                ScreenEntries.value
                                                    "count"
                                                    "label.payments"
                                                    Prominence.Primary
                                                    (PresentedValue.Count 12345L)
                                                ScreenEntries.value
                                                    "money"
                                                    "field.claimedAmount.label"
                                                    Prominence.Primary
                                                    (PresentedValue.money (
                                                        DomainInputs.money "1234.5" "EUR"
                                                    ))
                                                ScreenEntries.value
                                                    "date"
                                                    "field.incidentDate.label"
                                                    Prominence.Primary
                                                    (PresentedValue.Date(
                                                        DomainInputs.date "2026-11-04"
                                                    ))
                                            ]
                                    ]
                            Actions = []
                            Links = []
                            NextCursor = None
                            Basis = ObservationBasis.Snapshot DomainInputs.workBasis
                            Disclosure = HumanDisclosure.Current
                            History = None
                        }

                    use terminal =
                        new ShellConsoleFixture.Terminal(
                            [
                                ShellLine.Text "case list --language en --display-locale de-DE"
                                ShellLine.Text "case list --language ar --display-locale en-GB"
                                ShellLine.Text "exit"
                            ]
                        )

                    let calls = ResizeArray<byte array>()

                    let invoke frame dispatched _ =
                        calls.Add(Frames.inputBytes frame)
                        dispatched ()

                        CliReply.result
                            frame
                            (Api.caseList.Output.Write(
                                RepresentationReply.Human
                                    {
                                        HumanReadReply.Outcome =
                                            HumanReadOutcome.Succeeded document
                                    }
                            ))
                        |> required
                        |> Task.FromResult

                    use processor =
                        RemoteFrameProcessor.ForTest(
                            Stream.Null,
                            terminal.Output,
                            terminal.Errors,
                            invoke,
                            (fun _ -> Task.FromResult())
                        )

                    let shell = ShellSession(TimeProvider.System, terminal.Console, processor)

                    let code =
                        shell.Run().WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()

                    Expect.equal code 0 "Shell exits normally"
                    Expect.equal calls.Count 2 "One read for each explicit command"

                    Expect.equal
                        calls[0]
                        calls[1]
                        "Presentation preferences never enter query bytes"

                    let text = Encoding.UTF8.GetString(terminal.Output.ToArray())

                    for expected in
                        [
                            "12.345"
                            "1.234,50 EUR"
                            "4. Nov. 2026"
                            "12,345"
                            "1,234.50 EUR"
                            "4 Nov 2026"
                        ] do
                        Expect.stringContains
                            text
                            expected
                            "Supplied values use each command context"

                    Expect.stringContains
                        text
                        (MessageCatalogs.render "ar" "label.payments" Map.empty)
                        "Second command keeps Arabic wording")
            testCase
                "help uses the selected display locale for human bounds while keeping machine limits canonical"
                (fun () ->
                    let rendering = context "en" "ar-EG"
                    let text = Discovery.commandHelp rendering [ "note"; "add" ] |> Option.get
                    Expect.stringContains text "٢٬٠٠٠" "Localized note character bound"

                    Expect.isFalse
                        (text.Contains "2,000")
                        "English wording does not choose numeric conventions"

                    match
                        CliInvocations.admitConfiguredWith
                            (read
                                [
                                    "XDG_CONFIG_HOME",
                                    Path.Combine(
                                        Path.GetTempPath(),
                                        "claimcore-display-missing-" + Guid.NewGuid().ToString("N")
                                    )
                                    "CLAIMCORE_LANGUAGE", "en"
                                    "CLAIMCORE_DISPLAY_LOCALE", "de-DE"
                                ])
                            [ "help"; "note"; "add"; "--display-locale"; "ar-EG" ]
                        |> required
                    with
                    | CliInvocation.CommandHelp(_, actual, _) ->
                        Expect.equal actual rendering "Help keeps the resolved independent context"
                    | _ -> failtest "Command help required"

                    let bytes = Discovery.commandHelpJson rendering [ "note"; "add" ] |> Option.get
                    let raw = Encoding.UTF8.GetString bytes

                    Expect.stringContains
                        raw
                        "--display-locale"
                        "Help advertises its finite setting"

                    Expect.stringContains
                        (ReplyTextFormat.label rendering "endpoint.operation.get.meaning")
                        "١٠"
                        "Shared operation window follows display digits")
            testCase
                "forty-digit totals remain exact when language and display locale differ"
                (fun () ->
                    let total =
                        DecimalTotal.parse "1234567890123456789012345678901234567890.1234"
                        |> required

                    let currency = Currency.parse "EUR" |> required

                    Expect.equal
                        (ReplyTextFormat.moneyTotal (context "ar" "de-DE") currency total)
                        "1.234.567.890.123.456.789.012.345.678.901.234.567.890,1234 EUR"
                        "No floating point or precision loss")
            testCase
                "authored requests and offline frames are invariant across valid display locales"
                (fun () ->
                    let operation = "019aa6a0-0000-7000-8000-000000000001"

                    let arguments =
                        [
                            "case"
                            "record-payment"
                            "LOCALE"
                            "--expected-revision"
                            "3"
                            "--payment-date"
                            "2026-02-03"
                            "--paid-amount"
                            "1.2300"
                            "--paid-currency"
                            "EUR"
                            "--operation-id"
                            operation
                        ]

                    let frame locale =
                        Grammar.prepare
                            (fun () -> operation)
                            (arguments @ [ "--language"; "ar"; "--display-locale"; locale ])
                        |> required

                    let original = frame "en-GB"

                    let offline locale =
                        Grammar.prepareOffline (
                            arguments @ [ "--dry-run"; "--display-locale"; locale ]
                        )
                        |> required
                        |> Option.get
                        |> _.Bytes

                    for locale in ClaimCore.Presentation.LocalizationPolicy.displayLocales do
                        Expect.equal
                            (Frames.inputBytes (frame locale).Invocation)
                            (Frames.inputBytes original.Invocation)
                            "Request bytes keep exact input precision"

                        Expect.equal
                            ((frame locale).Bytes)
                            original.Bytes
                            "Machine frame carries no display preference"

                        Expect.equal
                            (offline locale)
                            (offline "en-GB")
                            "Offline grammar carries no presentation state")
        ]


[<Tests>]
let pendingDeadline =
    testCase
        "CLI pending lookup renders supplied semantic deadline with explicit independent locale"
        (fun () ->
            let finalAt = DateTimeOffset(2026, 11, 7, 10, 10, 0, TimeSpan.Zero)

            let document =
                { HumanDocument.empty HumanDisclosure.None with
                    Screen = OperationScreen.pending finalAt
                }

            let reply =
                RepresentationReply.Human
                    {
                        HumanOperationReply.OperationId =
                            Guid.Parse "10000000-0000-7000-8000-000000000001"
                        Outcome = HumanLookupResult.Pending(finalAt, document)
                    }

            let output language locale =
                OperationText.getLines (context language locale) reply |> String.concat "\n"

            let arabic = output "ar" "ar-EG"

            Expect.stringContains
                arabic
                "جارٍ التحقق"
                "Pending wording comes from the shared Arabic message"

            Expect.stringContains
                arabic
                "٢٠٢٦"
                "The canonical instant is rendered using explicit native digits"

            let british = output "ar" "en-GB"

            Expect.stringContains
                british
                "جارٍ التحقق"
                "Changing display locale does not change wording"

            Expect.stringContains
                british
                "7 Nov 2026 10:10 +00:00"
                "Display locale controls the pending deadline"

            Expect.stringContains
                (output "en" "en-US")
                "Nov 7, 2026"
                "US date order is distinct from British date order"

            Expect.isFalse
                (british.Contains("operationPending", StringComparison.Ordinal))
                "No message key leaks")
