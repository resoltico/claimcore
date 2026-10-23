module ClaimCore.Tests.WireRoundTripPropertyTests

open ClaimCore.Contract

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
        Requests: Map<Endpoint, WireRequest list>
        Replies: Map<Endpoint, WireReply list>
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
            collect seeds (recordContext kind field) field.PropertyType item)
    elif FSharpType.IsUnion kind then
        let case, fields = FSharpValue.GetUnionFields(value, kind)

        Array.zip (case.GetFields()) fields
        |> Array.iter (fun (field, item) ->
            let key =
                if isOption kind then
                    context
                else
                    unionContext kind case field

            collect seeds key field.PropertyType item)

let private admitted =
    function
    | Ok value -> value
    | Error _ -> failtest "The generated positive seed corpus must admit completely."

let private sameJson (original: JsonElement) bytes =
    use encoded = JsonDocument.Parse(ReadOnlyMemory<byte> bytes)

    Expect.isTrue
        (JsonElement.DeepEquals(original, encoded.RootElement))
        "Typed seed encoding retains every JSON value."

let private load () =
    let path = Path.Combine(RepositoryRoot.find (), "contracts/corpus.json")
    use source = JsonDocument.Parse(File.ReadAllBytes(path))
    let mutable requests, replies = Map.empty, Map.empty
    let values, tokens = Dictionary(), Dictionary()

    for sample in source.RootElement.GetProperty("cases").EnumerateArray() do
        if sample.GetProperty("valid").GetBoolean() then
            let endpoint =
                sample.GetProperty("endpoint").GetString()
                |> nonNull
                |> Endpoint.tryParse
                |> Option.get

            let bytes = sample.GetProperty("value").GetRawText() |> Encoding.UTF8.GetBytes

            if sample.GetProperty("direction").GetString() = "request" then
                let value = Admission.admit endpoint bytes |> admitted
                sameJson (sample.GetProperty "value") (Admission.encode value)

                requests <-
                    Map.change endpoint (fun old -> Some(value :: defaultArg old [])) requests
            else
                let value = Replies.decode endpoint bytes |> admitted
                sameJson (sample.GetProperty "value") (Replies.encode value)
                replies <- Map.change endpoint (fun old -> Some(value :: defaultArg old [])) replies

    let seeds =
        {
            Values = values
            ContextValues = Dictionary()
            Tokens = tokens
            Requests = requests
            Replies = replies
        }

    for KeyValue(_, entries) in requests do
        for entry in entries do
            collect seeds "request" typeof<WireRequest> (box entry)

    for KeyValue(_, entries) in replies do
        for entry in entries do
            collect seeds "reply" typeof<WireReply> (box entry)

    seeds

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
        || context.StartsWith("InstallationOffer.AuthorityAct", StringComparison.Ordinal)
    then
        Gen.item (source.Tokens[context] |> Seq.distinct |> Seq.toArray)
    elif field = "Cursor" then
        scalarText
    elif response then
        responseString field
    else
        arbitraryText

let private integer response context =
    let field = name context

    let low, high =
        if context.StartsWith("InstallationOffer.Open", StringComparison.Ordinal) then
            0L, 0L
        elif field = "Limit" then
            1L, 50L
        elif field = "MaximumCharacters" || field = "MaximumFractionalDigits" then
            0L, int64 Int32.MaxValue
        elif field = "MaximumPageSize" || field = "MaximumIntegerDigits" then
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

let private listBounds (context: string) =
    let maximum =
        match context with
        | "CaseAdvice.Acts" -> 12
        | "CaseAdvice.History"
        | "CorrectionChoices.Registration" -> 2
        | "ActorContext.Capabilities" -> 10
        | "ActorContext.Views"
        | "CorrectionChoices.Decision"
        | "CorrectionChoices.Payment" -> 3
        | "ActorContext.Offers"
        | "CasesPage.Offers" -> 4
        | "AdvisoryReview.Changes" -> 13
        | _ -> 50

    let minimum =
        if context.StartsWith("HistoryEntries.", StringComparison.Ordinal) then
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
        freshRecord response source kind value
    elif FSharpType.IsUnion kind then
        choose source context kind |> Gen.bind (freshUnion response source context kind)
    else
        invalidOp ("Unsupported typed Wire property node: " + kind.FullName)

and private freshList response source context kind =
    let item = kind.GetGenericArguments()[0]
    let minimum, maximum = listBounds context

    gen {
        let! size = count minimum maximum

        let entry =
            choose source context item |> Gen.bind (fresh response source context item)

        let! entries = Gen.list (Range.singleton size) entry
        return listValue kind entries
    }

and private freshRecord response source kind value =
    Array.zip (FSharpType.GetRecordFields kind) (FSharpValue.GetRecordFields(nonNull value))
    |> Array.map (fun (field, item) ->
        fresh response source (recordContext kind field) field.PropertyType item)
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
                unionContext kind case field

        fresh response source key field.PropertyType item)
    |> Array.toList
    |> sequence
    |> Gen.map (fun values -> FSharpValue.MakeUnion(case, List.toArray values))

let private requestProperty endpoint =
    let source = seeds.Value
    let values = source.Requests[endpoint]

    property {
        let! seed = Gen.item values

        let! value =
            freshUnion false source "request" typeof<WireRequest> (box seed)
            |> Gen.map unbox<WireRequest>

        return Admission.admit endpoint (Admission.encode value) = Ok value
    }

let private replyProperty endpoint =
    let source = seeds.Value
    let values = source.Replies[endpoint]

    property {
        let! seed = Gen.item values

        let! value =
            freshUnion true source "reply" typeof<WireReply> (box seed)
            |> Gen.map unbox<WireReply>

        return Replies.decode endpoint (Replies.encode value) = Ok value
    }

let private closedUnionCoverage (source: Seeds) =
    for KeyValue(kind, entries) in source.Values do
        if FSharpType.IsUnion kind && not (isOption kind) && not (isList kind) then
            let expected =
                FSharpType.GetUnionCases kind
                // Provisioning is record-only; correction has its own offer case, not an ordinary act payload.
                |> Array.filter (fun case ->
                    (kind <> typeof<AuthorityKind>
                     || case.Name <> nameof AuthorityKind.ProvisionOwner)
                    && (kind <> typeof<BusinessActKind>
                        || case.Name <> nameof BusinessActKind.CorrectCase))
                |> Array.map _.Tag
                |> Set.ofArray

            let actual =
                entries
                |> Seq.map (fun value -> FSharpValue.GetUnionFields(value, kind) |> fst |> _.Tag)
                |> Set.ofSeq

            Expect.equal
                actual
                expected
                ("Closed typed alternatives remain reachable: " + kind.Name)

let private seedCases () =
    let source = seeds.Value
    closedUnionCoverage source

    for endpoint in Endpoint.all do
        for value in source.Requests[endpoint] do
            Expect.isTrue
                (Admission.admit endpoint (Admission.encode value) = Ok value)
                "Every request seed alternative round trips."

        for value in source.Replies[endpoint] do
            Expect.isTrue
                (Replies.decode endpoint (Replies.encode value) = Ok value)
                "Every reply seed alternative round trips."

[<Tests>]
let tests =
    testList
        "typed Wire property round trips"
        [
            testCase "generated positive alternatives seed every endpoint and direction" seedCases
            for endpoint in Endpoint.all do
                testCase (Endpoint.id endpoint + " request DTO property") (fun () ->
                    run (wireId endpoint false) (requestProperty endpoint))

                testCase (Endpoint.id endpoint + " reply DTO property") (fun () ->
                    run (wireId endpoint true) (replyProperty endpoint))
        ]
