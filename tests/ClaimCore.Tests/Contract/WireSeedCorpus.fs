module ClaimCore.Tests.WireSeedCorpus

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


// The corpus supplies closed alternatives; generators below construct fresh typed values without
// consulting a codec or its schema. Business strings may violate today's admission policy.
[<NoEquality; NoComparison>]
type internal Seeds =
    {
        Values: Dictionary<Type, ResizeArray<obj | null>>
        ContextValues: Dictionary<string * Type, ResizeArray<obj | null>>
        Tokens: Dictionary<string, ResizeArray<string>>
        Requests: Map<string, JsonValue list>
        Replies: Map<string, JsonValue list>
    }

let internal isList (kind: Type) =
    kind.IsGenericType && kind.GetGenericTypeDefinition() = typedefof<list<_>>

let internal isOption (kind: Type) =
    kind.IsGenericType && kind.GetGenericTypeDefinition() = typedefof<option<_>>

let internal add (target: Dictionary<'Key, ResizeArray<'Value>>) key value =
    match target.TryGetValue key with
    | true, values -> values.Add value
    | false, _ -> target.Add(key, ResizeArray [ value ])

let internal recordContext (kind: Type) (field: Reflection.PropertyInfo) =
    kind.Name + "." + field.Name

let internal unionContext (kind: Type) (case: UnionCaseInfo) (field: Reflection.PropertyInfo) =
    kind.Name + "." + case.Name + "." + field.Name

let private retainedValues (kind: Type) value =
    if kind = typeof<Registration> then
        Registration.values (unbox value)
    elif kind = typeof<Decision> then
        Decision.values (unbox value)
    elif kind = typeof<Payment> then
        Payment.values (unbox value)
    else
        []

// Named accessors preserve the actual typed scalar alternatives hidden by opaque group representations.
let private groupFields (kind: Type) value =
    if kind = typeof<RegistrationInput> then
        RegistrationInput.values (unbox value) |> List.map fst
    elif kind = typeof<DecisionInput> then
        DecisionInput.values (unbox value) |> List.map fst
    elif kind = typeof<PaymentInput> then
        PaymentInput.values (unbox value) |> List.map fst
    else
        retainedValues kind value |> List.map fst

let rec internal collect (seeds: Seeds) context (kind: Type) (value: obj | null) =
    add seeds.Values kind value
    add seeds.ContextValues (context, kind) value

    for field in groupFields kind value do
        collect seeds (context + ".groupField") typeof<BusinessField> (box field)

    for field, scalar in retainedValues kind value do
        collect
            seeds
            (context + ".groupValue." + BusinessField.key field)
            typeof<Admitted>
            (box scalar)

    if kind = typeof<string> then
        add seeds.Tokens context (unbox<string> value)
    elif isList kind then
        let item = kind.GetGenericArguments()[0]

        for entry in (nonNull value) :?> IEnumerable do
            collect seeds context item entry
    elif kind = typeof<InputIssues> then
        collect
            seeds
            (context + ".Issues")
            typeof<InputIssue list>
            (InputIssues.toList (unbox<InputIssues> value) |> box)
    elif kind = typeof<PrincipalKey> then
        let input = PrincipalKey.toInput (unbox<PrincipalKey> value)
        collect seeds (context + ".Principal") typeof<PrincipalInput> (box input)
    elif kind = typeof<CaseSummaryFacts> then
        let facts = unbox<CaseSummaryFacts> value

        for KeyValue(field, value) in facts.Fields do
            collect seeds (context + ".listedField") typeof<BusinessField> (box field)

            collect
                seeds
                (context + ".listedValue." + BusinessField.key field)
                typeof<CaseFieldValue option>
                (box value)
    elif kind = typeof<RevisionConflictInfo> then
        let conflict = unbox<RevisionConflictInfo> value

        collect
            seeds
            (context + ".RevisionConflictInfo.ActualRevision")
            typeof<Revision>
            (box conflict.ActualRevision)

        collect
            seeds
            (context + ".RevisionConflictInfo.Changes")
            typeof<ConflictChanges option>
            (box conflict.Changes)
    elif kind = typeof<CaseAdvice> then
        let advice = unbox<CaseAdvice> value
        collect seeds (context + ".CaseAdvice.Acts") typeof<CaseActKind list> (box advice.Acts)

        collect
            seeds
            (context + ".CaseAdvice.Correction")
            typeof<CorrectionChoices option>
            (box advice.Correction)

        collect
            seeds
            (context + ".CaseAdvice.History")
            typeof<HistoryDetail list>
            (box advice.History)

        collect
            seeds
            (context + ".CaseAdvice.Blocked")
            typeof<CaseActRefusal list>
            (box advice.Blocked)
    elif FSharpType.IsTuple kind then
        Array.zip (FSharpType.GetTupleElements kind) (FSharpValue.GetTupleFields(nonNull value))
        |> Array.iteri (fun index (itemType, item) ->
            collect seeds (context + ".Item" + string (index + 1)) itemType item)
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

let internal admitted =
    function
    | Ok value -> value
    | Error _ ->
        failtest
            "The authored positive seed corpus must admit completely; see test: authored seed corpus admits every endpoint and direction."

let internal examples filename =
    let path = Path.Combine(RepositoryRoot.find (), "tests/fixtures", filename)
    use source = JsonDocument.Parse(File.ReadAllBytes(path))

    source.RootElement.GetProperty("cases").EnumerateArray()
    |> Seq.filter (fun sample -> sample.GetProperty("valid").GetBoolean())
    |> Seq.map (fun sample ->
        let endpoint = sample.GetProperty("endpoint").GetString() |> nonNull

        let value = PositiveExamples.value sample

        endpoint, value)
    |> Seq.groupBy fst
    |> Seq.map (fun (key, values) -> key, values |> Seq.map snd |> Seq.toList)
    |> Map.ofSeq

let internal collectEndpoint (source: Seeds) (endpoint: Endpoint<'Input, 'Output>) =
    for value in source.Requests[endpoint.Token] do
        collect source "request" typeof<'Input> (box (endpoint.Input.Read "$" value |> admitted))

    for value in source.Replies[endpoint.Token] do
        collect source "reply" typeof<'Output> (box (endpoint.Output.Read "$" value |> admitted))

let internal load () =
    let source =
        {
            Values = Dictionary()
            ContextValues = Dictionary()
            Tokens = Dictionary()
            Requests = examples "examples/requests.json"
            Replies = examples "examples/responses.json"
        }

    for definition in Api.definitions do
        let work =
            definition.Accept
                { new IEndpointVisitor<System.Action> with
                    member _.Visit<'Input, 'Output>(endpoint: Endpoint<'Input, 'Output>) =
                        System.Action(fun () -> collectEndpoint source endpoint)
                }

        work.Invoke()

    source

let internal seeds = lazy (load ())
