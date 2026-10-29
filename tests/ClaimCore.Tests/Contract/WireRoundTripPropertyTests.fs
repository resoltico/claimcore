module ClaimCore.Tests.WireRoundTripPropertyTests

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
open ClaimCore.Tests.WireValueGenerators


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

let private endpointProperty response (endpoint: Endpoint<'Input, 'Output>) =
    if response then
        codecProperty true endpoint.Output seeds.Value.Replies[endpoint.Token]
    else
        codecProperty false endpoint.Input seeds.Value.Requests[endpoint.Token]

let private propertyFor response (definition: IEndpointDefinition) =
    definition.Accept
        { new IEndpointVisitor<Property<bool>> with
            member _.Visit<'Input, 'Output>(endpoint: Endpoint<'Input, 'Output>) =
                endpointProperty response endpoint
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

                    // Diagnostic groups admit only Payment. Owner provisioning has its own
                    // host boundary; neither is a general request-codec alternative.
                    let cases =
                        FSharpType.GetUnionCases kind
                        |> Array.filter (fun case ->
                            // PaymentProgress is encoded as nullable members, not wire tags; its three
                            // representations have an independent boundary test.
                            kind <> typeof<PaymentProgress>
                            && (kind <> typeof<RecordGroup> || case.Name = "Payment")
                            && (kind <> typeof<AuthoredAuthorityAct>
                                || case.Name <> "ProvisionOwner"))

                    for case in cases do
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
        for value in source.Requests[endpoint.Token] do
            Expect.equal
                (endpoint.NormalizeInput value |> admitted |> CanonicalWriter.bytes)
                (CanonicalWriter.bytes value)
                "Every request seed round trips through its actual typed codec"

        for value in source.Replies[endpoint.Token] do
            Expect.equal
                (endpoint.NormalizeOutput value |> admitted |> CanonicalWriter.bytes)
                (CanonicalWriter.bytes value)
                "Every response seed round trips through its actual typed codec"

[<Tests>]
let tests =
    testList
        "typed Wire property round trips"
        [
            testCase "authored seed corpus admits every endpoint and direction" (fun () ->
                let source = seeds.Value

                Expect.equal
                    (source.Requests |> Map.keys |> Set.ofSeq)
                    (Inventory.tokens "endpoints" |> Set.ofList)
                    "Every inventoried endpoint has authored request seeds"

                Expect.equal
                    (source.Replies |> Map.keys |> Set.ofSeq)
                    (Inventory.tokens "endpoints" |> Set.ofList)
                    "Every inventoried endpoint has authored reply seeds")
            testCase "authored positive alternatives seed every endpoint and direction" seedCases
            for definition in Api.definitions do
                let endpoint = ClaimCore.Protocol.Route.ofDefinition definition

                testCase (Api.id endpoint + " request DTO property") (fun () ->
                    run (wireName endpoint false) (propertyFor false definition))

                testCase (Api.id endpoint + " reply DTO property") (fun () ->
                    run (wireName endpoint true) (propertyFor true definition))
        ]
