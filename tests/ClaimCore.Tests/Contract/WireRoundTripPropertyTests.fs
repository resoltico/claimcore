module ClaimCore.Tests.WireRoundTripPropertyTests

open ClaimCore.Contract
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
open ClaimCore.Contract.Protocol
open ClaimCore.TestSupport
open ClaimCore.Tests.PropertyHarness

// The corpus supplies closed alternatives; generators below construct fresh typed values without
// consulting a codec or its schema. Business strings may violate today's admission policy.
[<NoEquality; NoComparison>]
type private Seeds =
    {
        Values: Dictionary<Type, ResizeArray<obj | null>>
        ContextValues: Dictionary<string * Type, ResizeArray<obj | null>>
        Tokens: Dictionary<string, ResizeArray<string>>
        Requests: Map<string, JsonValue list>
        Replies: Map<string, JsonValue list>
    }

let private isList (kind: Type) =
    kind.IsGenericType && kind.GetGenericTypeDefinition() = typedefof<list<_>>

let private isOption (kind: Type) =
    kind.IsGenericType && kind.GetGenericTypeDefinition() = typedefof<option<_>>

let private add (target: Dictionary<'Key, ResizeArray<'Value>>) key value =
    match target.TryGetValue key with
    | true, values -> values.Add value
    | false, _ -> target.Add(key, ResizeArray [ value ])

let private recordContext (kind: Type) (field: Reflection.PropertyInfo) =
    kind.Name + "." + field.Name

let private unionContext (kind: Type) (case: UnionCaseInfo) (field: Reflection.PropertyInfo) =
    kind.Name + "." + case.Name + "." + field.Name

let rec private collect (seeds: Seeds) context (kind: Type) (value: obj | null) =
    add seeds.Values kind value
    add seeds.ContextValues (context, kind) value

    if kind = typeof<string> then
        add seeds.Tokens context (unbox<string> value)
    elif isList kind then
        let item = kind.GetGenericArguments()[0]

        for entry in (nonNull value) :?> IEnumerable do
            collect seeds context item entry
    elif FSharpType.IsRecord kind then
        Array.zip (FSharpType.GetRecordFields kind) (FSharpValue.GetRecordFields(nonNull value))
        |> Array.iter (fun (field, item) ->
            collect seeds (context + "." + recordContext kind field) field.PropertyType item)
    elif FSharpType.IsUnion kind then
        let case, fields = FSharpValue.GetUnionFields(value, kind)

        Array.zip (case.GetFields()) fields
        |> Array.iter (fun (field, item) ->
            let key =
                if isOption kind then
                    context
                else
                    context + "." + unionContext kind case field

            collect seeds key field.PropertyType item)

let private admitted =
    function
    | Ok value -> value
    | Error _ -> failtest "The authored positive seed corpus must admit completely."

let private examples filename =
    let path = Path.Combine(RepositoryRoot.find (), "tests/fixtures", filename)
    use source = JsonDocument.Parse(File.ReadAllBytes(path))

    source.RootElement.GetProperty("cases").EnumerateArray()
    |> Seq.filter (fun sample -> sample.GetProperty("valid").GetBoolean())
    |> Seq.map (fun sample ->
        let endpoint = sample.GetProperty("endpoint").GetString() |> nonNull

        let value =
            sample.GetProperty("value").GetRawText()
            |> Encoding.UTF8.GetBytes
            |> StrictJson.read Limits.MaximumResponseBytes
            |> admitted

        endpoint, value)
    |> Seq.groupBy fst
    |> Seq.map (fun (key, values) -> key, values |> Seq.map snd |> Seq.toList)
    |> Map.ofSeq

let private load () =
    let source =
        {
            Values = Dictionary()
            ContextValues = Dictionary()
            Tokens = Dictionary()
            Requests = examples "wire-request-examples.json"
            Replies = examples "wire-response-examples.json"
        }

    for endpoint in Api.all do
        endpoint.Accept
            { new IEndpointVisitor<bool> with
                member _.Visit(endpoint: Endpoint<'Input, 'Output>) =
                    for value in source.Requests[endpoint.Id] do
                        let typed = endpoint.Input.Read "$" value |> admitted
                        collect source "request" typeof<'Input> (box typed)

                    for value in source.Replies[endpoint.Id] do
                        let typed = endpoint.Output.Read "$" value |> admitted
                        collect source "reply" typeof<'Output> (box typed)

                    true
            }
        |> ignore

    source

let private seeds = lazy (load ())

let private sequence generators =
    List.foldBack
        (fun generator state ->
            gen {
                let! head = generator
                let! tail = state
                return head :: tail
            })
        generators
        (Gen.constant [])

let private scalarText =
    let atom =
        Gen.item
            [
                "x"
                "Ž"
                "東京"
                "😀"
                "e\u0301"
                "\""
                "\\"
                "\u0001"
                "\b"
                "\t"
                "\n"
                "\f"
                "\r"
                "\u001f"
                "\u2028"
                "\u2029"
                " "
            ]

    Gen.list (Range.constant 1 16) atom |> Gen.map (String.concat "")

let private arbitraryText = Gen.frequency [ 1, Gen.constant ""; 4, scalarText ]

let private guid =
    Gen.guid
    |> Gen.map (fun value ->
        if value = Guid.Empty then
            Guid.Parse "10000000-0000-4000-8000-000000000001"
        else
            value)

let private date =
    Gen.int32 (Range.constant DateOnly.MinValue.DayNumber DateOnly.MaxValue.DayNumber)
    |> Gen.map (fun value ->
        DateOnly.FromDayNumber(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

let private instant =
    Gen.int64 (Range.constant 0L (DateTimeOffset.MaxValue.Ticks / 10L))
    |> Gen.map (fun value -> DateTimeOffset(value * 10L, TimeSpan.Zero))

let private amount =
    gen {
        let! whole = Gen.uint64 (Range.constant 0UL 999_999_999_999_999_999UL)
        let! fraction = Gen.uint32 (Range.constant 0u 9999u)

        return
            whole.ToString(CultureInfo.InvariantCulture)
            + "."
            + fraction.ToString("D4", CultureInfo.InvariantCulture)
    }

let private uppercase length =
    Gen.array (Range.singleton length) (Gen.char 'A' 'Z') |> Gen.map String

let private digest =
    Gen.array
        (Range.singleton 64)
        (Gen.item [ '0' .. '9' ]
         |> fun numbers -> Gen.choice [ numbers; Gen.char 'a' 'f' ])
    |> Gen.map String

let private reference = Gen.string (Range.constant 1 80) (Gen.char '!' '~')

let private dates =
    set
        [
            "IncidentDate"
            "IncidentNotificationDate"
            "PaymentDecisionDate"
            "PaymentDate"
            "BusinessDate"
        ]

let private tokens =
    set
        [
            "Kind"
            "Act"
            "act"
            "Role"
            "Status"
            "Disposition"
            "Detail"
            "Field"
            "RecommendedAction"
            "History"
            "Capabilities"
            "Views"
            "Registration"
            "Decision"
            "Payment"
        ]

let private name (context: string) = context.Split('.') |> Array.last

let private responseStrings =
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

let private responseString field =
    if Set.contains field dates then
        date
    else
        Map.tryFind field responseStrings |> Option.defaultValue scalarText

let private generatedString response (source: Seeds) context =
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

let private integer response context =
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

let private listBounds (path: string) =
    let segments = path.Split('.')

    let context =
        segments |> Array.skip (max 0 (segments.Length - 2)) |> String.concat "."

    let maximum =
        Map.ofList
            [
                "AuthorityState.Grants", 1
                "CaseAdvice.Acts", 12
                "CaseAdvice.History", 2
                "CorrectionChoices.Registration", 2
                "ActorContext.Capabilities", 10
                "ActorContext.Views", 3
                "CorrectionChoices.Decision", 3
                "CorrectionChoices.Payment", 3
                "ActorContext.Offers", 4
                "CaseSummaryPage.Offers", 4
                "AdvisoryReview.Changes", 13
            ]
        |> Map.tryFind context
        |> Option.defaultValue 50

    let minimum =
        if path.Contains("HistoryEntries.", StringComparison.Ordinal) then
            1
        else
            0

    minimum, maximum

let private count minimum maximum =
    Gen.frequency
        [
            1, Gen.constant minimum
            1, Gen.constant maximum
            3, Gen.int32 (Range.constant minimum (min maximum (minimum + 3)))
        ]

let private listValue (kind: Type) entries =
    let cases = FSharpType.GetUnionCases kind
    let empty = cases |> Array.find (fun case -> case.GetFields().Length = 0)
    let cons = cases |> Array.find (fun case -> case.GetFields().Length = 2)

    List.foldBack
        (fun head tail -> FSharpValue.MakeUnion(cons, [| head; tail |]))
        entries
        (FSharpValue.MakeUnion(empty, [||]))

let private choose (source: Seeds) context kind =
    let values =
        match source.ContextValues.TryGetValue((context, kind)) with
        | true, values -> values
        | false, _ -> source.Values[kind]

    values |> Seq.toArray |> Gen.item

let rec private fresh response source context (kind: Type) (value: obj | null) =
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

let private codecProperty response (codec: Codec<'T>) samples =
    let source = seeds.Value
    let context = if response then "reply" else "request"

    property {
        let! seed = Gen.item samples
        let typed = codec.Read "$" seed |> admitted

        let! value =
            fresh response source context typeof<'T> (box typed) |> Gen.map unbox<'T>

        let maximum =
            if response then
                Limits.MaximumResponseBytes
            else
                Limits.MaximumRequestBytes

        return
            match Codec.encode codec value |> Codec.decode maximum codec with
            | Ok decoded -> Unchecked.equals decoded value
            | Error _ -> false

    }

let private requestProperty (endpoint: AnyEndpoint) =
    endpoint.Accept
        { new IEndpointVisitor<Property<bool>> with
            member _.Visit endpoint =
                codecProperty false endpoint.Input seeds.Value.Requests[endpoint.Id]
        }

let private replyProperty (endpoint: AnyEndpoint) =
    endpoint.Accept
        { new IEndpointVisitor<Property<bool>> with
            member _.Visit endpoint =
                codecProperty true endpoint.Output seeds.Value.Replies[endpoint.Id]
        }

let private closedUnionCoverage (source: Seeds) =
    let missing =
        [
            for KeyValue(kind, entries) in source.Values do
                if FSharpType.IsUnion kind && not (isOption kind) && not (isList kind) then
                    let actual =
                        entries
                        |> Seq.map (fun value ->
                            FSharpValue.GetUnionFields(value, kind) |> fst |> _.Tag)
                        |> Set.ofSeq

                    for case in FSharpType.GetUnionCases kind do
                        if not (Set.contains case.Tag actual) then
                            yield kind.Name + "." + case.Name
        ]

    Expect.isEmpty
        missing
        ("Closed alternatives missing from authored examples: "
         + String.concat ", " missing)

let private seedCases () =
    let source = seeds.Value
    closedUnionCoverage source

    for endpoint in Api.all do
        endpoint.Accept
            { new IEndpointVisitor<bool> with
                member _.Visit endpoint =
                    for value in source.Requests[endpoint.Id] do
                        endpoint.Input.Read "$" value
                        |> admitted
                        |> endpoint.Input.Write
                        |> CanonicalWriter.bytes
                        |> fun encoded ->
                            Expect.equal
                                encoded
                                (CanonicalWriter.bytes value)
                                "Every request seed round trips."

                    for value in source.Replies[endpoint.Id] do
                        endpoint.Output.Read "$" value
                        |> admitted
                        |> endpoint.Output.Write
                        |> CanonicalWriter.bytes
                        |> fun encoded ->
                            Expect.equal
                                encoded
                                (CanonicalWriter.bytes value)
                                "Every response seed round trips."

                    true
            }
        |> ignore

[<Tests>]
let tests =
    testList
        "typed Wire property round trips"
        [
            testCase "authored positive alternatives seed every endpoint and direction" seedCases
            for endpoint in Api.all do
                testCase (Api.id endpoint + " request DTO property") (fun () ->
                    run (wireId endpoint false) (requestProperty endpoint))

                testCase (Api.id endpoint + " reply DTO property") (fun () ->
                    run (wireId endpoint true) (replyProperty endpoint))
        ]
