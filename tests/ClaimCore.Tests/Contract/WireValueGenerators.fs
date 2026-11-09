module ClaimCore.Tests.WireValueGenerators

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

open System
open System.Collections
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open Microsoft.FSharp.Reflection
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.TestSupport
open ClaimCore.Tests.PropertyHarness
open ClaimCore.Tests.WireSeedCorpus


open ClaimCore.Tests.WireScalarGenerators
open ClaimCore.Tests.WireGroupGenerators

let internal listBounds (path: string) =
    let segments = path.Split('.')

    let context =
        segments |> Array.skip (max 0 (segments.Length - 2)) |> String.concat "."

    let maximum =
        Map.ofList
            [
                "CaseListFilter`1.Lifecycles",
                ((Vocabulary.entries Lifecycle.vocabulary |> List.map snd)).Length
                "CaseListFilter`1.Statuses",
                ((Vocabulary.entries CaseStatus.vocabulary |> List.map snd)).Length
                "CaseListFilter`1.Progress",
                ((Vocabulary.entries CaseProgress.vocabulary |> List.map snd)).Length
                "CaseWorklistCounts.Lifecycles",
                ((Vocabulary.entries Lifecycle.vocabulary |> List.map snd)).Length
                "CaseWorklistCounts.Statuses",
                ((Vocabulary.entries CaseStatus.vocabulary |> List.map snd)).Length
                "CaseWorklistCounts.Progress",
                ((Vocabulary.entries CaseProgress.vocabulary |> List.map snd)).Length
                "CaseWorklistCounts.Assignment", 2
                "CaseWorklistCounts.Due", 5
                "CaseTimelinePage.Items", 10
                "ActorReviewItem.Roles", 7
                "ActorContext.Roles", 7
                "AuthorityState.Grants", 48
                "AuthorityAdmission`2.Grants", 48
                "AuthorityReview.Effects", 50
                "AuthorityReceipt.Effects", 50
                "AuthorityState.Access", 11
                "CapabilityAccess.DirectRoles", 7
                "CapabilityAccess.InstallationRoles", 7
                "CaseAdvice.Acts", ((CaseActKind.all |> List.map CaseActKind.token)).Length
                "ReminderReadModel.Acts",
                ((Vocabulary.entries ReminderActKind.vocabulary |> List.map snd)).Length
                "CorrectionChoices.Registration", 2
                "ActorContext.Capability", 11
                "ActorContext.View", 3
                "CorrectionChoices.Decision", 3
                "ActorContext.Offers", 4
                "CaseSummaryPage.Offers", 4
                "WorkViews.CaseViews", 11
                "WorkViews.ReminderViews", 7
                "CaseStageOutline.Completed", 4
                "Tracker.steps", 4
                "CaseReadModel.Attention", 6
                "CaseSummary.Attention", 6
                "CaseReviewChanges.Fields", CaseFields.declarations.Length
                "ReviewPresentation.Changes", 324
                "ReviewPresentation.Unchanged", 324
                "CaseNotes.Items", 20
                "TimelineDisclosure.Items", CaseFields.declarations.Length + 1
            ]
        |> Map.tryFind context
        |> Option.defaultValue 50

    let minimum =
        if context = "AuthorityAdmission`2.Grants" then 1
        elif context = "CaseStageOutline.Completed" then 1
        elif context = "Tracker.steps" then 4
        else 0

    minimum, maximum

let internal count minimum maximum =
    Gen.frequency
        [
            1, Gen.constant minimum
            1, Gen.constant maximum
            3, Gen.int32 (Range.constant minimum (min maximum (minimum + 3)))
        ]

let internal listValue (kind: Type) entries =
    let cases = FSharpType.GetUnionCases kind
    let empty = cases |> Array.find (fun case -> case.GetFields().Length = 0)
    let cons = cases |> Array.find (fun case -> case.GetFields().Length = 2)

    List.foldBack
        (fun head tail -> FSharpValue.MakeUnion(cons, [| head; tail |]))
        entries
        (FSharpValue.MakeUnion(empty, [||]))

let internal choose (source: Seeds) context kind =
    let values =
        match source.ContextValues.TryGetValue((context, kind)) with
        | true, values -> values
        | false, _ -> source.Values[kind]

    values |> Seq.toArray |> Gen.item

let private financialValue (kind: Type) =
    if kind = typeof<Currency> then
        Some(
            uppercase Currency.Characters
            |> Gen.map (Currency.parse >> Result.defaultWith (string >> invalidOp) >> box)
        )
    elif kind = typeof<DecimalTotal> then
        Some(
            gen {
                let! digits = Gen.int32 (Range.constant 1 DecimalTotal.MaximumIntegerDigits)
                let! first = Gen.char '1' '9'
                let! rest = Gen.string (Range.singleton (digits - 1)) (Gen.char '0' '9')

                let! fraction =
                    Gen.string
                        (Range.constant 0 DecimalTotal.MaximumFractionalDigits)
                        (Gen.char '0' '9')

                let raw = string first + rest + (if fraction = "" then "" else "." + fraction)
                return DecimalTotal.parse raw |> Result.defaultWith (string >> invalidOp) |> box
            }
        )
    elif kind = typeof<Money> then
        Some(
            gen {
                let! amount = amount
                let! currency = uppercase 3
                return box (CaseFixtures.money amount currency)
            }
        )
    else
        None


let private scalarDomainValue (kind: Type) =
    if kind = typeof<CaseReference> then
        Some(reference |> Gen.map (CaseFixtures.reference >> box))
    elif kind = typeof<OperationId> then
        Some(
            guid
            |> Gen.map (
                OperationIds.fromEntropy (DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero))
                >> CaseFixtures.operationId
                >> box
            )
        )
    elif kind = typeof<CaseId> then
        Some(guid |> Gen.map (CaseFixtures.caseId >> box))
    elif kind = typeof<NoteId> then
        Some(guid |> Gen.map (NoteId.create >> Option.get >> box))
    elif kind = typeof<NoteText> then
        Some(
            Gen.constant (
                NoteText.parse "A current note"
                |> Result.defaultWith (string >> invalidOp)
                |> box
            )
        )
    elif kind = typeof<PaymentEntry> then
        Some(
            gen {
                let! identity = guid
                let! calendar = date

                return
                    PaymentEntry.create
                        (OperationIds.fromEntropy
                            (DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero))
                            identity
                         |> CaseFixtures.operationId)
                        (CaseFixtures.date calendar)
                        (Amount.parse "1" |> Result.defaultWith (string >> invalidOp))
                    |> Result.defaultWith invalidOp
                    |> box
            }
        )
    elif kind = typeof<ActorId> then
        Some(guid |> Gen.map (CaseFixtures.actorId >> box))
    elif kind = typeof<CalendarDate> then
        Some(date |> Gen.map (CaseFixtures.date >> box))
    elif kind = typeof<CountryCode> then
        Some(uppercase 2 |> Gen.map (CaseFixtures.country >> box))
    elif kind = typeof<PartyName> then
        Some(scalarText |> Gen.map (CaseFixtures.name >> box))
    elif kind = typeof<ContactText> then
        Some(
            scalarText
            |> Gen.map (ContactText.parse >> Result.defaultWith (string >> invalidOp) >> box)
        )
    elif kind = typeof<EntryHash> then
        Some(
            gen {
                let! first = guid
                let! second = guid

                return
                    Array.append (first.ToByteArray()) (second.ToByteArray())
                    |> EntryHash.ofBytes
                    |> Option.get
                    |> box
            }
        )
    elif kind = typeof<PrincipalKey> then
        Some(
            gen {
                let! id = guid
                let! human = Gen.bool
                let kind = if human then PrincipalKind.Human else PrincipalKind.Service

                return
                    PrincipalKey.ofKind
                        kind
                        ("https://issuer-" + id.ToString("N") + ".example/")
                        (id.ToString("N"))
                    |> Result.defaultWith (string >> invalidOp)
                    |> box
            }
        )
    else
        financialValue kind

let private claimsValue =
    gen {
        let! name = Gen.option scalarText
        let! username = Gen.option scalarText
        let! email = Gen.option scalarText
        return RequesterClaims.admit name username email
    }

let internal domainValue kind =
    if kind = typeof<RequesterClaims> then
        Some(claimsValue |> Gen.map box)
    elif kind = typeof<UnregisteredRequester> then
        Some(
            gen {
                let! id = guid
                let! claims = claimsValue

                return
                    box
                        {
                            UnregisteredRequester.Issuer =
                                "https://issuer-" + id.ToString("N") + ".example/"
                            Subject = id.ToString("N")
                            Claims = claims
                        }
            }
        )
    else
        match groupValue kind with
        | Some value -> Some value
        | None ->
            match WireWorkGenerators.domainValue kind with
            | Some value -> Some value
            | None -> scalarDomainValue kind

let private positionValue response context kind =
    if kind = typeof<Revision> then
        Some(integer response context |> Gen.map (CaseFixtures.revision >> box))
    elif kind = typeof<Sequence> then
        Some(integer response context |> Gen.map (CaseFixtures.sequence >> box))
    else
        None

let private maintenanceValue = Gen.bool |> Gen.map RegisterMaintenance.create

let private caseFieldValue value =
    let represented parse project generator =
        generator
        |> Gen.map (parse >> Result.defaultWith (string >> invalidOp) >> project)

    match value with
    | CaseFieldValue.Reference _ ->
        represented CaseReference.parse CaseFieldValue.Reference reference
    | CaseFieldValue.Status _ ->
        Gen.item [ CaseStatus.Opened; CaseStatus.Closed ]
        |> Gen.map CaseFieldValue.Status
    | CaseFieldValue.Business scalar ->
        let value =
            match scalar with
            | Admitted.Date _ -> represented CalendarDate.parse Admitted.Date date
            | Admitted.Country _ -> represented CountryCode.parse Admitted.Country (uppercase 2)
            | Admitted.Name _ -> represented PartyName.parse Admitted.Name scalarText
            | Admitted.Identifier _ ->
                represented Identifier.parse Admitted.Identifier (Gen.constant "Policy 42")
            | Admitted.Description _ ->
                represented IncidentDescription.parse Admitted.Description scalarText
            | Admitted.Amount _ -> represented Amount.parse Admitted.Amount amount
            | Admitted.Currency _ -> represented Currency.parse Admitted.Currency (uppercase 3)

        value |> Gen.map CaseFieldValue.Business

let private listedFacts (facts: CaseSummaryFacts) =
    facts.Fields
    |> Map.toList
    |> List.map (fun (field, value) ->
        match value with
        | None -> Gen.constant (field, None)
        | Some value -> caseFieldValue value |> Gen.map (fun value -> field, Some value))
    |> sequence
    |> Gen.map (Map.ofList >> fun fields -> { CaseSummaryFacts.Fields = fields })

// Presentation carries structure that reflection cannot generate independently (notably table width).
let private presentedValue =
    gen {
        let! text = scalarText
        let! count = Gen.int64 (Range.constant 0L JsonValue.MaximumInteger)
        let! calendar = date
        let! observed = instant
        let! financial = amount
        let! headline = Gen.item CaseHeadline.all

        let! value =
            Gen.item
                [
                    PresentedValue.Money
                        {
                            Value = CaseFixtures.money financial "EUR"
                            DisplayScale = 4
                        }
                    PresentedValue.MoneyTotal
                        {
                            Currency =
                                Currency.parse "EUR" |> Result.defaultWith (string >> invalidOp)
                            Value =
                                DecimalTotal.parse financial
                                |> Result.defaultWith (string >> invalidOp)
                            DisplayScale = 4
                        }
                    PresentedValue.Date(CaseFixtures.date calendar)
                    PresentedValue.Instant observed
                    PresentedValue.Party(CaseFixtures.name "Name")
                    PresentedValue.Actor
                        {
                            ActorId =
                                CaseFixtures.actorId (
                                    System.Guid.Parse("10000000-0000-7000-8000-000000000001")
                                )
                            DisplayName = DisplayName.Named(CaseFixtures.name "Name")
                            PrincipalKind = PrincipalKind.Human
                            Availability = ActorAvailability.Available
                        }
                    PresentedValue.State headline
                    PresentedValue.Reference(CaseFixtures.reference "GENERIC-1")
                    PresentedValue.Text text
                    PresentedValue.Narrative text
                    PresentedValue.Identifier text
                    PresentedValue.Message "label.details"
                    PresentedValue.Code("correction", "KEEP")
                    PresentedValue.Codes("act", [ "OPEN"; "REMINDER_CREATE" ])
                    PresentedValue.Count count
                    PresentedValue.Absent "label.notRecorded"
                ]

        return value
    }

let private presentedSection =
    gen {
        let! values = Gen.list (Range.constant 0 3) presentedValue

        return
            {
                Section.TitleId = "label.details"
                Entries =
                    values
                    |> List.mapi (fun index value ->
                        {
                            Entry.Key = "entry" + string index
                            LabelId = "label.details"
                            Prominence = Prominence.Primary
                            Value = value
                        })
            }
    }

let private presentedView =
    Gen.choice
        [
            Gen.list (Range.constant 0 3) presentedSection
            |> Gen.map ScreenEntries.factScreen
            gen {
                let! observedAt = instant
                let! calendar = date
                let! reached = Gen.bool
                let! reason = presentedValue

                let! content =
                    Gen.item
                        [
                            RegionContent.Stages
                                {
                                    Current = CaseStageKind.Opened
                                    Steps =
                                        [
                                            {
                                                Stage = CaseStageKind.Opened
                                                Reached = reached
                                                Date = Some(CaseFixtures.date calendar)
                                                Amount = None
                                                Meter = None
                                            }
                                        ]
                                }
                            RegionContent.Codes("attention", [ "OVERDUE" ])
                            RegionContent.Actions
                                {
                                    Recommended = Some(ActKind.Case CaseActKind.Decide)
                                    Groups = []
                                }
                            RegionContent.Notice
                                {
                                    MessageId = "label.recorded"
                                    Reason = Some reason
                                    Tone = NoticeTone.Warning
                                }
                            RegionContent.Timeline
                                [
                                    {
                                        Key = "record-1"
                                        When = observedAt
                                        Who =
                                            Inputs.actorReference (
                                                CaseFixtures.actorId (
                                                    Guid.Parse
                                                        "10000000-0000-4000-8000-000000000001"
                                                )
                                            )
                                        Summary =
                                            {
                                                MessageId = "label.recorded"
                                                Arguments = Map.empty
                                            }
                                        Highlights = []
                                        Details = []
                                    }
                                ]
                        ]

                return
                    Screen.create
                        [
                            {
                                Role = RegionRole.Facts
                                TitleId = None
                                Content = content
                            }
                        ]
            }
            gen {
                let! width = Gen.int32 (Range.constant 0 3)

                let! rows =
                    Gen.list (Range.constant 0 3) (Gen.list (Range.singleton width) presentedValue)

                let columns =
                    [
                        for index in 1..width ->
                            {
                                Column.Key = "column" + string index
                                LabelId = "label.details"
                                Prominence = Prominence.Primary
                                IsMoney = false
                            }
                    ]

                return
                    Screen.create
                        [
                            {
                                Role = RegionRole.Results
                                TitleId = None
                                Content =
                                    RegionContent.Table
                                        {
                                            Columns = columns
                                            Groups = []
                                            Sections = []
                                            Meter = None
                                            Rows =
                                                rows
                                                |> List.mapi (fun index values ->
                                                    {
                                                        RegionRow.Key = "row" + string index
                                                        Values = values
                                                        Detail = None
                                                    })
                                        }
                            }
                        ]

            }
        ]

// Declared context, editable seeds and their task references form one grammar unit.
// Vary the supplied facts/observation while preserving that unit; independent mutation tests
// exercise missing, duplicate and mismatched interaction references as refusal cases.
let private bindDocumentActions context (screen: Screen) =
    let tasks =
        match context with
        | InteractionContext.Case value -> value.Tasks
        | InteractionContext.Requester value -> value.Tasks
        | InteractionContext.Page value -> value.Tasks
        | InteractionContext.Reminder value -> value.Tasks
        | InteractionContext.Empty
        | InteractionContext.PaymentPage _
        | InteractionContext.Continuation _
        | InteractionContext.Review _
        | InteractionContext.Receipt _ -> []

    let offered =
        tasks
        |> List.tryPick (function
            | TaskId.Operation act -> Some act
            | TaskId.Query _ -> None)

    let mutable used = false

    screen.Regions
    |> List.map (fun region ->
        match region.Content with
        | RegionContent.Actions _ ->
            let recommended = if used then None else offered
            used <- true

            { region with
                Content =
                    RegionContent.Actions
                        {
                            Recommended = recommended
                            Groups = []
                        }
            }
        | _ -> region)
    |> Screen.create

let private presentationGenerator kind =
    if kind = typeof<Screen> then
        Some(presentedView |> Gen.map box)
    elif kind = typeof<Section> then
        Some(presentedSection |> Gen.map box)
    elif kind = typeof<PresentedValue> then
        Some(presentedValue |> Gen.map box)
    else
        None

let rec internal fresh response source context (kind: Type) (value: obj | null) =
    match presentationGenerator kind with
    | Some generator -> generator
    | None ->
        match positionValue response context kind with
        | Some generator -> generator
        | None ->
            match domainValue kind with
            | Some generator -> generator
            | None -> freshRepresentation response source context kind value

and private freshRepresentation response source context (kind: Type) (value: obj | null) =
    if kind = typeof<HumanDocument> then
        gen {
            let document = unbox<HumanDocument> value
            let! screen = presentedView

            let! basis =
                match document.Basis with
                | ObservationBasis.Unavailable -> Gen.constant ObservationBasis.Unavailable
                | ObservationBasis.Snapshot observed ->
                    fresh
                        response
                        source
                        (context + ".Observation")
                        typeof<WorkDateBasis>
                        (box observed)
                    |> Gen.map (unbox<WorkDateBasis> >> ObservationBasis.Snapshot)

            return
                box
                    { document with
                        Screen = bindDocumentActions document.Context screen
                        Basis = basis
                    }
        }
    elif kind = typeof<string> then
        generatedString response source context |> Gen.map box
    elif kind = typeof<Guid> then
        guid |> Gen.map box
    elif kind = typeof<int> then
        integer response context |> Gen.map (int >> box)
    elif kind = typeof<int64> then
        integer response context |> Gen.map box
    elif kind = typeof<DateOnly> then
        Gen.int32 (Range.constant DateOnly.MinValue.DayNumber DateOnly.MaxValue.DayNumber)
        |> Gen.map (DateOnly.FromDayNumber >> box)
    elif kind = typeof<DateTimeOffset> then
        instant |> Gen.map box
    elif kind = typeof<bool> then
        Gen.bool |> Gen.map box
    elif kind = typeof<unit> then
        Gen.constant (box ())
    elif isList kind then
        freshList response source context kind
    elif kind = typeof<RegisterMaintenance> then
        maintenanceValue |> Gen.map box
    elif kind = typeof<CaseFieldValue> then
        caseFieldValue (unbox value) |> Gen.map box
    elif kind = typeof<CaseSummaryFacts> then
        listedFacts (unbox value) |> Gen.map box
    elif kind = typeof<RevisionConflictInfo> then
        let conflict = unbox<RevisionConflictInfo> value

        gen {
            let! revision =
                integer response (context + ".RevisionConflictInfo.ActualRevision")
                |> Gen.map CaseFixtures.revision

            let! changes =
                fresh
                    response
                    source
                    (context + ".RevisionConflictInfo.Changes")
                    typeof<ConflictChanges>
                    (box (conflict.Changes |> Option.get))
                |> Gen.map unbox<ConflictChanges>

            return RevisionConflictInfo.at revision |> RevisionConflictInfo.explain changes |> box
        }
    elif kind = typeof<CaseAdvice> then
        let advice = unbox<CaseAdvice> value

        gen {
            let! acts =
                fresh
                    response
                    source
                    (context + ".CaseAdvice.Acts")
                    typeof<CaseActKind list>
                    (box advice.Acts)
                |> Gen.map unbox<CaseActKind list>

            let! history =
                fresh
                    response
                    source
                    (context + ".CaseAdvice.History")
                    typeof<HistoryAccess>
                    (box advice.History)
                |> Gen.map unbox<HistoryAccess>

            let! correction =
                if List.contains CaseActKind.Correct acts then
                    Gen.constant (
                        Some
                            {
                                CorrectionChoices.Registration = Amendment.actions
                                Decision = Change.actions
                            }
                    )
                else
                    Gen.constant None

            let! maintenance = maintenanceValue

            let! recommended =
                if maintenance.Active || List.isEmpty acts then
                    Gen.constant None
                else
                    Gen.choice [ Gen.constant None; Gen.item acts |> Gen.map Some ]

            let blocked =
                CaseActKind.all
                |> List.filter (fun act -> not (List.contains act acts))
                |> List.map (fun act ->
                    {
                        CaseActRefusal.Act = act
                        Reason = Refusal.Access AccessRefusal.ResourceUnavailable
                    })

            return
                CaseAdvice.create acts correction history blocked
                |> Result.defaultWith invalidOp
                |> CaseAdvice.duringMaintenance maintenance
                |> CaseAdvice.withRecommendation recommended
                |> Result.defaultWith invalidOp
                |> box
        }
    elif kind = typeof<InputIssues> then
        gen {
            let! size = Gen.int32 (Range.constant 1 DiagnosticCodecs.MaximumInputIssues)
            let! seed = choose source (context + ".Issues") typeof<InputIssue>
            let item = fresh response source (context + ".Issues") typeof<InputIssue> seed
            let! items = Gen.list (Range.singleton size) item
            return items |> List.map unbox<InputIssue> |> InputIssues.ofList |> Option.get |> box
        }
    elif kind = typeof<PreviewReply> then
        freshRecord response source context kind value
        |> Gen.map (unbox<PreviewReply> >> WireWorkGenerators.coherentPreview >> box)
    elif kind = typeof<OperationReceipt> then
        freshRecord response source context kind value
        |> Gen.map (unbox<OperationReceipt> >> WireWorkGenerators.coherentReceipt >> box)
    elif FSharpType.IsTuple kind then
        Array.zip (FSharpType.GetTupleElements kind) (FSharpValue.GetTupleFields(nonNull value))
        |> Array.mapi (fun index (itemType, item) ->
            fresh response source (context + ".Item" + string (index + 1)) itemType item)
        |> Array.toList
        |> sequence
        |> Gen.map (fun values -> FSharpValue.MakeTuple(List.toArray values, kind))
    elif FSharpType.IsRecord kind then
        freshRecord response source context kind value
    elif FSharpType.IsUnion kind then
        choose source context kind |> Gen.bind (freshUnion response source context kind)
    else
        invalidOp ("Unsupported typed Wire property node: " + kind.FullName)

and private freshList response source context kind =
    let item = kind.GetGenericArguments()[0]
    let minimum, maximum = listBounds context

    if not (source.Values.ContainsKey item) then
        // Empty authored lists do not invent an element template. Boundary tests own overproduction.
        Gen.constant (listValue kind [])
    else
        gen {
            let! size = count minimum maximum

            let entry =
                choose source context item |> Gen.bind (fresh response source context item)

            let! entries = Gen.list (Range.singleton size) entry
            return listValue kind entries
        }

and private freshRecord response source context kind value =
    if kind = typeof<TimelineDisclosure> then
        let seed = unbox<TimelineDisclosure> value

        gen {
            let! items =
                freshList
                    response
                    source
                    (context + ".TimelineDisclosure.Items")
                    typeof<TimelineChange list>
                |> Gen.map unbox<TimelineChange list>

            let! event =
                fresh
                    response
                    source
                    (context + ".TimelineDisclosure.Overview.TimelineOverview.Event")
                    typeof<TimelineEvent>
                    (box seed.Overview.Event)
                |> Gen.map unbox<TimelineEvent>

            let! highlights = WireWorkGenerators.timelineHighlights items

            return
                box
                    {
                        TimelineDisclosure.Items = items
                        Overview =
                            {
                                Event = event
                                Highlights = highlights
                            }
                    }
        }
    else
        Array.zip (FSharpType.GetRecordFields kind) (FSharpValue.GetRecordFields(nonNull value))
        |> Array.map (fun (field, item) ->
            fresh
                response
                source
                (context + "." + recordContext kind field)
                field.PropertyType
                item)
        |> Array.toList
        |> sequence
        |> Gen.map (fun values -> FSharpValue.MakeRecord(kind, List.toArray values))

and private freshUnion response source context kind value =
    freshUnionFields response source context kind value

and private freshUnionFields response source context kind value =
    let case, fields = FSharpValue.GetUnionFields(value, kind)

    Array.zip (case.GetFields()) fields
    |> Array.map (fun (field, item) ->
        let key =
            if isOption kind then
                context
            else
                context + "." + unionContext kind case field

        fresh response source key field.PropertyType item)
    |> Array.toList
    |> sequence
    |> Gen.map (fun values -> FSharpValue.MakeUnion(case, List.toArray values))
