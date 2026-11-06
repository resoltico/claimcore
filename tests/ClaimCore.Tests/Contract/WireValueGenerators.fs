module ClaimCore.Tests.WireValueGenerators

open ClaimCore.Protocol
open ClaimCore.Protocol.Presentation
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
                "CaseListFilter`1.Lifecycles", (Inventory.tokens "caseLifecycles").Length
                "CaseListFilter`1.Statuses", (Inventory.tokens "caseStatuses").Length
                "CaseListFilter`1.Progress", (Inventory.tokens "caseProgress").Length
                "CaseWorklistCounts.Lifecycles", (Inventory.tokens "caseLifecycles").Length
                "CaseWorklistCounts.Statuses", (Inventory.tokens "caseStatuses").Length
                "CaseWorklistCounts.Progress", (Inventory.tokens "caseProgress").Length
                "CaseWorklistCounts.Assignment", 2
                "CaseWorklistCounts.Due", 5
                "ActorReviewItem.Roles", 7
                "ActorContext.Roles", 7
                "AuthorityState.Grants", 48
                "AuthorityAdmission`2.Grants", 48
                "AuthorityReview.Effects", 50
                "AuthorityReceipt.Effects", 50
                "AuthorityState.Access", 11
                "CapabilityAccess.DirectRoles", 7
                "CapabilityAccess.InstallationRoles", 7
                "CaseAdvice.Acts", (Inventory.tokens "caseActs").Length
                "CaseAdvice.History", 2
                "ReminderReadModel.Acts", (Inventory.tokens "reminderActs").Length
                "CorrectionChoices.Registration", 2
                "ActorContext.Capability", 11
                "ActorContext.View", 3
                "CorrectionChoices.Decision", 3
                "CorrectionChoices.Payment", 4
                "ActorContext.Offers", 4
                "CaseSummaryPage.Offers", 4
                "CaseViews.Items", 11
                "ReminderViews.Items", 7
                "CaseStageOutline.Completed", 4
                "Tracker.steps", 4
                "CaseReadModel.Attention", 6
                "CaseSummary.Attention", 6
                "CaseReviewChanges.Fields", Inventory.fieldCount
                "ReviewPresentation.Changes", 324
                "ReviewPresentation.Unchanged", 324
                "HistoryDifference.Fields", Inventory.fieldCount
                "Disclosed.Item", Inventory.fieldCount + 1
                "TimelineDisclosure.Items", CaseFields.declarations.Length + 1
            ]
        |> Map.tryFind context
        |> Option.defaultValue 50

    let minimum =
        if context = "AuthorityAdmission`2.Grants" then
            1
        elif context = "CaseStageOutline.Completed" then
            1
        elif context = "Tracker.steps" then
            4
        elif path.Contains("HistoryEntries.", StringComparison.Ordinal) then
            1
        else
            0

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
        Some(guid |> Gen.map (CaseFixtures.operationId >> box))
    elif kind = typeof<CaseId> then
        Some(guid |> Gen.map (CaseFixtures.caseId >> box))
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

let private maintenanceValue =
    gen {
        let! active = Gen.bool

        let! generation =
            Gen.int64 (Range.constant (if active then 1L else 0L) 9007199254740991L)

        return RegisterMaintenance.create active generation |> Result.defaultWith invalidOp
    }

let private listedScalar value =
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
        | Some value -> listedScalar value |> Gen.map (fun value -> field, Some value))
    |> sequence
    |> Gen.map (Map.ofList >> fun fields -> { CaseSummaryFacts.Fields = fields })

let rec internal fresh response source context (kind: Type) (value: obj | null) =
    match positionValue response context kind with
    | Some generator -> generator
    | None ->
        match domainValue kind with
        | Some generator -> generator
        | None -> freshRepresentation response source context kind value

and private freshRepresentation response source context (kind: Type) (value: obj | null) =
    if kind = typeof<string> then
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
                    typeof<HistoryDetail list>
                    (box advice.History)
                |> Gen.map unbox<HistoryDetail list>

            let! correction =
                if List.contains CaseActKind.Correct acts then
                    choose source (context + ".CaseAdvice.Correction") typeof<CorrectionChoices>
                    |> Gen.bind (
                        fresh
                            response
                            source
                            (context + ".CaseAdvice.Correction")
                            typeof<CorrectionChoices>
                    )
                    |> Gen.map (unbox<CorrectionChoices> >> Some)
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
    elif kind = typeof<PresentedPreviewReply> then
        freshRecord response source context kind value
        |> Gen.map (unbox<PresentedPreviewReply> >> WireWorkGenerators.coherentPreview >> box)
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

    gen {
        let! size = count minimum maximum
        let! seed = choose source context item

        let entry =
            if item = typeof<HistoryEntry> then
                freshUnion response source context item seed
            else
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
    if kind = typeof<PresentedValue> then
        match unbox<PresentedValue> value with
        | PresentedValue.Code(vocabulary, _) ->
            presentationToken vocabulary
            |> Gen.map (fun token -> box (PresentedValue.Code(vocabulary, token)))
        | PresentedValue.Codes(vocabulary, _) ->
            Gen.list (Range.constant 0 11) (presentationToken vocabulary)
            |> Gen.map (fun tokens -> box (PresentedValue.Codes(vocabulary, tokens)))
        | _ -> freshUnionFields response source context kind value
    else
        freshUnionFields response source context kind value

and private presentationToken vocabulary =
    if vocabulary = "country" then
        uppercase 2
    elif vocabulary = "currency" then
        uppercase 3
    else
        [
            Vocabulary.name Lifecycle.vocabulary, Vocabulary.tokens Lifecycle.vocabulary
            Vocabulary.name CaseStatus.vocabulary, Vocabulary.tokens CaseStatus.vocabulary
            Vocabulary.name Role.vocabulary, Vocabulary.tokens Role.vocabulary
            Vocabulary.name ScopeKind.vocabulary, Vocabulary.tokens ScopeKind.vocabulary
            Vocabulary.name Capability.vocabulary, Vocabulary.tokens Capability.vocabulary
            Vocabulary.name ReminderState.vocabulary, Vocabulary.tokens ReminderState.vocabulary
            Vocabulary.name PresentedBoolean.vocabulary,
            Vocabulary.tokens PresentedBoolean.vocabulary
        ]
        |> Map.ofList
        |> Map.find vocabulary
        |> Gen.item

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
