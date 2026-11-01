module ClaimCore.Tests.WireValueGenerators

open ClaimCore.Protocol
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
                "AuthorityState.Grants", 1
                "AuthorityState.Access", 11
                "CapabilityAccess.DirectRoles", 7
                "CapabilityAccess.InstallationRoles", 7
                "CaseAdvice.Acts", (Inventory.tokens "caseActs").Length
                "CaseAdvice.History", 2
                "CorrectionChoices.Registration", 2
                "ActorContext.Capability", 11
                "ActorContext.View", 3
                "CorrectionChoices.Decision", 3
                "CorrectionChoices.Payment", 4
                "ActorContext.Offers", 4
                "CaseSummaryPage.Offers", 4
                "AdvisoryReview.Changes", Inventory.fieldCount
                "HistoryDifference.Fields", Inventory.fieldCount
            ]
        |> Map.tryFind context
        |> Option.defaultValue 50

    let minimum =
        if path.Contains("HistoryEntries.", StringComparison.Ordinal) then
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

let internal domainValue kind =
    match groupValue kind with
    | Some value -> Some value
    | None -> scalarDomainValue kind

let private positionValue response context kind =
    if kind = typeof<Revision> then
        Some(integer response context |> Gen.map (CaseFixtures.revision >> box))
    elif kind = typeof<Sequence> then
        Some(integer response context |> Gen.map (CaseFixtures.sequence >> box))
    else
        None

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

            return CaseAdvice.create acts correction history |> Result.defaultWith invalidOp |> box
        }
    elif kind = typeof<InputIssues> then
        gen {
            let! size = Gen.int32 (Range.constant 1 DiagnosticCodecs.MaximumInputIssues)
            let! seed = choose source (context + ".Issues") typeof<InputIssue>
            let item = fresh response source (context + ".Issues") typeof<InputIssue> seed
            let! items = Gen.list (Range.singleton size) item
            return items |> List.map unbox<InputIssue> |> InputIssues.ofList |> Option.get |> box
        }
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
    Array.zip (FSharpType.GetRecordFields kind) (FSharpValue.GetRecordFields(nonNull value))
    |> Array.map (fun (field, item) ->
        fresh response source (context + "." + recordContext kind field) field.PropertyType item)
    |> Array.toList
    |> sequence
    |> Gen.map (fun values -> FSharpValue.MakeRecord(kind, List.toArray values))

and private freshUnion response source context kind value =
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
