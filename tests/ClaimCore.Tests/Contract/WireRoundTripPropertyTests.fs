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

let private actorProperty response actor =
    match actor with
    | (ActorEndpoint.Operation OperationEndpoint.Preview) ->
        endpointProperty response Api.operationPreview
    | (ActorEndpoint.Operation OperationEndpoint.Execute) ->
        endpointProperty response Api.operationExecute
    | (ActorEndpoint.Query QueryEndpoint.CaseGet) -> endpointProperty response Api.caseGet
    | (ActorEndpoint.Query QueryEndpoint.CaseList) -> endpointProperty response Api.caseList
    | (ActorEndpoint.Query QueryEndpoint.CaseHistory) -> endpointProperty response Api.caseHistory
    | (ActorEndpoint.Query QueryEndpoint.Observe) -> endpointProperty response Api.operationObserve
    | (ActorEndpoint.Query QueryEndpoint.Resolve) -> endpointProperty response Api.operationResolve
    | (ActorEndpoint.Query QueryEndpoint.AuthorityList) ->
        endpointProperty response Api.authorityList
    | (ActorEndpoint.Query QueryEndpoint.AuthorityGrants) ->
        endpointProperty response Api.authorityGrants
    | (ActorEndpoint.Query QueryEndpoint.Context) -> endpointProperty response Api.context

let private propertyFor response (route: ClaimCore.Protocol.Route) =
    match route.Id with
    | EndpointId.Host HostEndpoint.Session -> endpointProperty response Api.session
    | EndpointId.Host HostEndpoint.Logout -> endpointProperty response Api.logout
    | EndpointId.Actor actor -> actorProperty response actor

let private requestProperty route = propertyFor false route
let private replyProperty route = propertyFor true route

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
            testCase "authored positive alternatives seed every endpoint and direction" seedCases
            for endpoint in Api.all do
                testCase (Api.id endpoint + " request DTO property") (fun () ->
                    run (wireName endpoint false) (requestProperty endpoint))

                testCase (Api.id endpoint + " reply DTO property") (fun () ->
                    run (wireName endpoint true) (replyProperty endpoint))
        ]
