module ClaimCore.Tests.WorkDescriptorTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

let private basis =
    {
        ObservedAt = DateTimeOffset.Parse "2026-11-04T12:00:00Z"
        BusinessDate = DateOnly(2026, 11, 4)
        TimeZoneId = "Europe/Riga"
    }

[<Tests>]
let tests =
    testList
        "typed work descriptors"
        [
            testCase
                "an unavailable installation zone preserves an explicitly UTC instant"
                (fun () ->
                    let output =
                        TextObservation.line
                            (RenderingContext.forLanguage "en")
                            { basis with
                                TimeZoneId = "unavailable-test-zone"
                            }

                    Expect.equal
                        output
                        "Observed 4 Nov 2026 12:00 +00:00 UTC · business date 4 Nov 2026 (unavailable-test-zone)"
                        "The supplied business zone is retained and the instant is never relabelled as that zone")
            testCase "aggregate money preserves forty integer digits and ISO minor units" (fun () ->
                let total raw =
                    DecimalTotal.parse raw |> CaseBuilders.accepted

                let currency raw =
                    Currency.parse raw |> CaseBuilders.accepted

                let large = "9999999999999999999999999999999999999999.1234"

                let actual =
                    TextDescriptorValue.render
                        (RenderingContext.forLanguage "en")
                        (PresentedValue.moneyTotal (currency "EUR") (total large))

                Expect.equal
                    actual
                    "9,999,999,999,999,999,999,999,999,999,999,999,999,999.1234 EUR"
                    "No decimal narrowing or rounded digits"

                for code, raw, expected in
                    [
                        "EUR", "0", "0.00 EUR"
                        "JPY", "150000", "150,000 JPY"
                        "EUR", "1234.5678", "1,234.5678 EUR"
                        "USD", "0.01", "0.01 USD"
                    ] do
                    Expect.equal
                        (TextDescriptorValue.render
                            (RenderingContext.forLanguage "en")
                            (PresentedValue.moneyTotal (currency code) (total raw)))
                        expected
                        "Currency minimums retain all meaningful fractional digits"

                let columns =
                    [
                        {
                            EntryDefinition.Key = "total"
                            LabelId = "label.total"
                            Prominence = Prominence.Primary
                            IsMoney = true
                            Read =
                                fun () -> PresentedValue.moneyTotal (currency "EUR") (total large)
                        }
                    ]

                for width in [ 1; 10; 100 ] do
                    let rendered =
                        TextTable.rows
                            (RenderingContext.forLanguage "en")
                            { Width = width; BoldHeader = false }
                            columns
                            [ () ]
                        |> String.concat "\n"

                    Expect.stringContains
                        rendered
                        actual
                        "Aggregate money is never truncated, including narrow cards")
            testCase "calendar dates and country names use the explicit display language" (fun () ->
                let date = DomainInputs.date "2026-11-04"

                Expect.equal
                    (TextDescriptorValue.render
                        (RenderingContext.forLanguage "en")
                        (PresentedValue.Date date))
                    "4 Nov 2026"
                    "Human dates are localized rather than wire literals"

                for language, expected in
                    [ "en", "Latvia (LV)"; "lv", "Latvija (LV)"; "ar", "لاتفيا (LV)" ] do
                    Expect.equal
                        (TextDescriptorValue.render
                            (RenderingContext.forLanguage language)
                            (PresentedValue.Code("country", "LV")))
                        expected
                        "Country names retain their identifying code without ambient culture mutation")
            testCase
                "observation uses the snapshot installation zone and localized business date"
                (fun () ->
                    Expect.equal
                        (TextObservation.line (RenderingContext.forLanguage "en") basis)
                        "Observed 4 Nov 2026 14:00 +02:00 · business date 4 Nov 2026 (Europe/Riga)"
                        "The actual basis timestamp is converted using the installation zone, with no client clock"

                    for language, observations in
                        [
                            "en", [ "4 Nov 2026 14:00 +02:00" ]
                            "lv", [ "4 nov. 2026 14:00 +02:00" ]
                            "ar",
                            [ "4 نوفمبر 2026 2:00\u00a0م +02:00"; "4 نوفمبر 2026 2:00 م +02:00" ]
                        ] do
                        let output =
                            TextObservation.line (RenderingContext.forLanguage language) basis

                        Expect.isTrue
                            (observations |> List.exists (fun observed -> output.Contains observed))
                            (sprintf
                                "The same exact observation instant is localized in every language. Expected one of %A in %s"
                                observations
                                output)

                        Expect.isFalse
                            (output.Contains "cli.text." || output.Contains "{observed}")
                            "No missing message key or interpolation survives")
            testCase
                "combined view counts retain identities, zero values and producer order"
                (fun () ->
                    let counts: WorkViews =
                        {
                            Basis = basis
                            CaseViews =
                                [
                                    { View = CaseView.Mine; Count = 0L }
                                    { View = CaseView.All; Count = 9L }
                                ]
                            ReminderViews =
                                [
                                    {
                                        View = ReminderView.Cancelled
                                        Count = 2L
                                    }
                                ]
                        }

                    let screen = WorkViewsScreen.view counts

                    Expect.equal
                        (screen.Regions |> List.map _.Role)
                        [ RegionRole.Results ]
                        "One combined results region"

                    match screen.Regions.Head.Content with
                    | RegionContent.Table table ->
                        Expect.equal
                            (table.Rows |> List.map _.Key)
                            [ "CASE:MINE"; "CASE:ALL"; "REMINDER:CANCELLED" ]
                            "Stable identities distinguish the two vocabularies"

                        Expect.equal
                            (table.Rows |> List.map (fun row -> List.last row.Values))
                            [
                                PresentedValue.Count 0L
                                PresentedValue.Count 9L
                                PresentedValue.Count 2L
                            ]
                            "Zero and producer order are retained"
                    | _ -> failtest "Expected a result table")
            testCase "full case facts retain non-listed fields and pair money once" (fun () ->
                let reference = DomainInputs.reference "CARD-1"

                let facts: CaseFields =
                    {
                        CaseReference = reference
                        Handling = Handling.Open
                        Registration = DomainInputs.admittedRegistration DomainInputs.registration
                        Finance = FinancialPosition.undecided
                    }

                let snapshot =
                    {
                        CaseId =
                            CaseFixtures.caseId (Guid.Parse "80000000-0000-7000-8000-000000000001")
                        Reference = reference
                        Revision = CaseFixtures.revision 1L
                        State = CaseState.Active facts
                    }

                let sections = CaseScreen.caseFacts snapshot
                let entries = sections |> List.collect _.Entries

                Expect.equal
                    (sections |> List.map _.TitleId)
                    [ "group.registration.label"; "group.decision.label" ]
                    "Facts keep the core group order"

                Expect.isTrue
                    (entries |> List.exists (fun entry -> entry.Key = "incidentDate"))
                    "Full cards retain facts omitted from list rows"

                Expect.equal
                    (entries
                     |> List.filter (fun entry ->
                         match entry.Value with
                         | PresentedValue.Money _ -> true
                         | _ -> false)
                     |> List.length)
                    1
                    "Claimed money is one typed value and the absent decision stays absent"

                Expect.isFalse
                    (entries |> List.exists (fun entry -> entry.Key = "claimedCurrency"))
                    "The currency is not duplicated")
            testCase "erased case cards suppress leftover work and facts" (fun () ->
                let state = CaseState.Erased

                let reply: CaseReadModel =
                    {
                        MatchedReference = None
                        Parties = []
                        PartyOffers = []
                        RelatedCases = RelatedCaseLinks.empty
                        FamilySequence = Sequence.create 1L |> Option.get
                        Snapshot =
                            {
                                CaseId =
                                    CaseFixtures.caseId (
                                        Guid.Parse "80000000-0000-7000-8000-000000000002"
                                    )
                                Reference = DomainInputs.reference "ERASED-1"
                                Revision = CaseFixtures.revision 2L
                                State = state
                            }
                        Work =
                            { Inputs.workView state with
                                Summary = HandlingSummary.parse "SECRET" |> Result.toOption
                            }
                        Advice = Inputs.caseAdvice state
                        LatestPayments = []
                        PaymentPayees = []
                        Mine = false
                        Headline = CaseHeadline.Erased
                        Attention = []
                        Stages = CaseStageDisplay.Erased
                        Disposition = None
                        NextActions = []
                        Notes =
                            {
                                Items = []
                                Count = 0L
                                CanAdd = false
                            }
                        ChangedAt = basis.ObservedAt
                        RecordedBy = ClaimCore.TestSupport.ResponseExamples.caseRead.RecordedBy
                    }

                let screen = CaseScreen.view reply

                Expect.equal
                    (screen.Regions |> List.map _.Role)
                    [ RegionRole.Identity; RegionRole.Notice; RegionRole.Details ]
                    "Erased screens contain identity, final notice and technical details"

                match screen.Regions.Head.Content with
                | RegionContent.Entries entries ->
                    Expect.equal
                        (entries |> List.map _.Key)
                        [ "caseReference" ]
                        "Only the reference identifies the erased record"
                | _ -> failtest "Expected identity entries"

                Expect.isFalse
                    (ViewText.render (RenderingContext.forLanguage "en") (CaseScreen.view reply)
                     |> List.exists (fun line -> line.Contains "SECRET"))
                    "Actual human output cannot disclose stale work")
            testCase "reminder descriptor preserves actor and due date as typed values" (fun () ->
                let actorId =
                    CaseFixtures.actorId (Guid.Parse "80000000-0000-7000-8000-000000000003")

                let actor = DomainInputs.namedActorReference actorId "Aija"
                let due = DomainInputs.date "2026-11-05"

                let reminder =
                    Reminder.decode
                        (ReminderId.ofOperationId (
                            CaseFixtures.operationId (
                                Guid.Parse "80000000-0000-7000-8000-000000000004"
                            )
                        ))
                        (CaseFixtures.caseId (Guid.Parse "80000000-0000-7000-8000-000000000005"))
                        (CaseFixtures.revision 1L)
                        (ReminderAction.parse "Telephone claimant"
                         |> Result.defaultWith (fun error -> failtestf "%A" error))
                        due
                        ReminderKind.FollowUp
                        ReminderState.Pending
                        None
                    |> Result.defaultWith failtest

                let reply: ReminderReadModel =
                    {
                        Reminder = reminder
                        Reference = DomainInputs.reference "REM-1"
                        Claimants =
                            [
                                (PartyName.parse "Synthetic claimant"
                                 |> Result.defaultWith (string >> failtest))
                            ]
                        Responsible = None
                        RecordedAt = basis.ObservedAt
                        RecordedBy = actor
                        Offers =
                            [
                                Inputs.offeredTask
                                    "reminder.complete"
                                    (Binding.Reminder(
                                        CaseSelector.Id(Reminder.caseId reminder),
                                        Reminder.id reminder,
                                        Reminder.revision reminder
                                    ))
                                    Map.empty
                            ]
                        Active = true
                        Timing = Some ReminderDue.Upcoming
                    }

                let entries =
                    ReminderScreen.view { Basis = basis; Item = reply }
                    |> _.Regions
                    |> List.collect (fun region ->
                        match region.Content with
                        | RegionContent.Entries entries -> entries
                        | _ -> [])

                let field key =
                    entries |> List.find (fun entry -> entry.Key = key) |> _.Value

                Expect.equal
                    (field "dueDate")
                    (PresentedValue.Date due)
                    "The CLI need not parse a date from arbitrary text"

                Expect.equal
                    (field "recordedBy")
                    (PresentedValue.Actor actor)
                    "Names retain the core ActorRef"

                Expect.equal
                    (field "timing")
                    (PresentedValue.Code("reminderDue", "UPCOMING"))
                    "Timing is core-owned"

                Expect.isFalse
                    (entries |> List.exists (fun entry -> entry.Key = "parentState"))
                    "An active parent has no invented inactivity row")
        ]
