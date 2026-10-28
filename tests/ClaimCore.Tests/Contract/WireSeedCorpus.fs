module ClaimCore.Tests.WireSeedCorpus

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

let rec internal collect (seeds: Seeds) context (kind: Type) (value: obj | null) =
    add seeds.Values kind value
    add seeds.ContextValues (context, kind) value

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
    | Error _ -> failtest "The authored positive seed corpus must admit completely."

let internal examples filename =
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
            Requests = examples "wire-request-examples.json"
            Replies = examples "wire-response-examples.json"
        }

    collectEndpoint source Api.session
    collectEndpoint source Api.logout
    collectEndpoint source Api.context
    collectEndpoint source Api.caseGet
    collectEndpoint source Api.caseList
    collectEndpoint source Api.caseHistory
    collectEndpoint source Api.operationPreview
    collectEndpoint source Api.operationExecute
    collectEndpoint source Api.operationObserve
    collectEndpoint source Api.operationResolve
    collectEndpoint source Api.authorityList
    collectEndpoint source Api.authorityGrants

    source

let internal seeds = lazy (load ())
