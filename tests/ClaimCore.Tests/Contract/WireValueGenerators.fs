module ClaimCore.Tests.WireValueGenerators

open ClaimCore.Protocol
open ClaimCore.Domain
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


let internal sequence generators =
    List.foldBack
        (fun generator state ->
            gen {
                let! head = generator
                let! tail = state
                return head :: tail
            })
        generators
        (Gen.constant [])

let internal scalarText =
    let atom =
        Gen.item
            [
                "x"
                "Ž"
                "東京"
                "😀"
                "é"
                "\""
                "\\"
                "\u0001"
                "\b"
                "\t"
                "\n"
                "\f"
                "\r"
                "\u001f"
                " "
                " "
                " "
            ]

    Gen.list (Range.constant 1 16) atom |> Gen.map (String.concat "")

let internal arbitraryText = Gen.frequency [ 1, Gen.constant ""; 4, scalarText ]

let internal guid =
    Gen.guid
    |> Gen.map (fun value ->
        if value = Guid.Empty then
            Guid.Parse "10000000-0000-4000-8000-000000000001"
        else
            value)

let internal date =
    Gen.int32 (Range.constant DateOnly.MinValue.DayNumber DateOnly.MaxValue.DayNumber)
    |> Gen.map (fun value ->
        DateOnly.FromDayNumber(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

let internal instant =
    Gen.int64 (Range.constant 0L (DateTimeOffset.MaxValue.Ticks / 10L))
    |> Gen.map (fun value -> DateTimeOffset(value * 10L, TimeSpan.Zero))

let internal amount =
    gen {
        let! whole = Gen.uint64 (Range.constant 0UL 999_999_999_999_999_999UL)
        let! fraction = Gen.uint32 (Range.constant 0u 9999u)

        return
            whole.ToString(CultureInfo.InvariantCulture)
            + "."
            + fraction.ToString("D4", CultureInfo.InvariantCulture)
    }

let internal uppercase length =
    Gen.array (Range.singleton length) (Gen.char 'A' 'Z') |> Gen.map String

let internal digest =
    Gen.array
        (Range.singleton 64)
        (Gen.item [ '0' .. '9' ]
         |> fun numbers -> Gen.choice [ numbers; Gen.char 'a' 'f' ])
    |> Gen.map String

let internal reference = Gen.string (Range.constant 1 80) (Gen.char '!' '~')

let internal dates =
    set
        [
            "IncidentDate"
            "IncidentNotificationDate"
            "PaymentDecisionDate"
            "PaymentDate"
            "BusinessDate"
        ]

let internal tokens =
    set
        [
            "Kind"
            "Act"
            "act"
            "Role"
            "Status"
            "Detail"
            "Field"
            "RecommendedAction"
            "History"
            "Capability"
            "View"
            "Registration"
            "Decision"
            "Payment"
        ]

let internal name (context: string) = context.Split('.') |> Array.last

let internal responseStrings =
    Map.ofList
        [
            "CaseReference", reference
            "caseReference", reference
            "Reference", reference
            "IncidentCountry", uppercase 2
            "ClaimedCurrency", uppercase 3
            "PayableCurrency", uppercase 3
            "ClaimedAmount", amount
            "PayableAmount", amount
            "EntryHash", digest
            "ContractIdentity", digest
            "Issuer", arbitraryText
            "Subject", arbitraryText
        ]

let internal responseString field =
    if Set.contains field dates then
        date
    else
        Map.tryFind field responseStrings |> Option.defaultValue scalarText

let internal generatedString response (source: Seeds) context =
    let field = name context

    if
        Set.contains field tokens
        || context.Contains("InstallationOffer.AuthorityAct", StringComparison.Ordinal)
    then
        Gen.item (source.Tokens[context] |> Seq.distinct |> Seq.toArray)
    elif field = "Cursor" || field = "AfterCursor" then
        scalarText
    elif response then
        responseString field
    else
        arbitraryText

let internal integer response context =
    let field = name context

    let low, high =
        if context.Contains("InstallationOffer.Open", StringComparison.Ordinal) then
            0L, 0L
        elif field = "Limit" then
            1L, 50L
        elif
            Set.contains
                field
                (set
                    [
                        "MaximumCharacters"
                        "MaximumFractionalDigits"
                        "maximumCharacters"
                        "fractionDigits"
                    ])
        then
            0L, int64 Int32.MaxValue
        elif
            Set.contains
                field
                (set
                    [
                        "MaximumPageSize"
                        "MaximumIntegerDigits"
                        "maximumPageSize"
                        "integerDigits"
                    ])
        then
            1L, int64 Int32.MaxValue
        elif not response || field = "ExpectedRevision" || field = "expectedRevision" then
            0L, JsonValue.MaximumInteger
        else
            1L, JsonValue.MaximumInteger

    Gen.frequency
        [
            1, Gen.constant low
            1, Gen.constant high
            3, Gen.int64 (Range.constant low high)
        ]

let internal listBounds (path: string) =
    let segments = path.Split('.')

    let context =
        segments |> Array.skip (max 0 (segments.Length - 2)) |> String.concat "."

    let maximum =
        Map.ofList
            [
                "AuthorityState.Grants", 1
                "CaseAdvice.Acts", (Inventory.tokens "caseActs").Length
                "CaseAdvice.History", 2
                "CorrectionChoices.Registration", 2
                "ActorContext.Capability", 10
                "ActorContext.View", 3
                "CorrectionChoices.Decision", 3
                "CorrectionChoices.Payment", 3
                "ActorContext.Offers", 4
                "CaseSummaryPage.Offers", 4
                "AdvisoryReview.Changes", Inventory.fieldCount
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

let internal domainValue (kind: Type) =
    if kind = typeof<CaseReference> then
        Some(reference |> Gen.map (CaseFixtures.reference >> box))
    elif kind = typeof<OperationId> then
        Some(guid |> Gen.map (CaseFixtures.operationId >> box))
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
