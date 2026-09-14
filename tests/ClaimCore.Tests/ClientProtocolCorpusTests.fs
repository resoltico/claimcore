module ClaimCore.Tests.ClientProtocolCorpusTests

open System
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClientProtocolFixtures

module Client = ClaimCore.Protocol.WebV2

let private corpusConformance () =
    let cases = corpus ()
    Expect.isGreaterThan cases.Length 100 "The shared corpus is actually loaded"
    let mutable acceptedEndpoints = Set.empty

    for case in cases do
        let endpoint = case.GetProperty("endpoint")
        let expected = case.GetProperty("valid").GetBoolean()
        let value = case.GetProperty("value").GetRawText() |> utf8

        let identifier =
            if endpoint.ValueKind = JsonValueKind.Null then
                None
            else
                Some(nonNull (endpoint.GetString()))

        let codec =
            identifier
            |> Option.map responseCodec
            |> Option.defaultValue (box Client.hostFailure |> nonNull)

        let roundTrip = runCodec codec value
        let status = case.GetProperty("status").GetInt32()

        let statusAllowed =
            identifier.IsSome || List.contains status Client.hostFailureStatuses

        Expect.equal
            (roundTrip.IsSome && statusAllowed)
            expected
            (nonNull (case.GetProperty("id").GetString()))

        if expected then
            roundTrip |> Option.iter (jsonEqual value)

            identifier
            |> Option.iter (fun value -> acceptedEndpoints <- Set.add value acceptedEndpoints)

    Expect.equal
        acceptedEndpoints
        (Client.endpoints |> List.map _.Identifier |> Set.ofList)
        "Every endpoint's typed response was exercised"

let private bodyDescription actual expected =
    match actual, expected with
    | ClaimCore.Protocol.RequestBody.None, None
    | ClaimCore.Protocol.RequestBody.Json, Some(JsonBody _) -> ()
    | ClaimCore.Protocol.RequestBody.Raw(media, maximum, headers),
      Some(RawBody(expectedMedia, expectedMaximum, expectedHeaders)) ->
        Expect.equal
            (media, maximum, headers)
            (expectedMedia, expectedMaximum, List.map fst expectedHeaders)
            "Raw byte/header requirements retain their owner"
    | _ -> failtest "Endpoint body kind changed in the client projection"

let private catalog () =
    let projection = ContractProjection.current ()
    Expect.equal (List.length Client.endpoints) 19 "Independent current endpoint count"

    Expect.equal
        Client.wireFingerprint
        "a2c311c629a7429e134ff873e02d3a0541485d22cd1d8c487c06c25c9eb529d0"
        "F02 does not change the existing service wire contract"

    Expect.equal
        (Client.endpoints |> List.map _.Identifier)
        (projection.WebEndpoints |> List.map _.Identifier)
        "Complete binding inventory"

    for actual, expected in List.zip Client.endpoints projection.WebEndpoints do
        bodyDescription actual.Body expected.Body

        Expect.equal
            (actual.Method, actual.Path, actual.SuccessMediaType)
            (expected.Method, expected.Path, expected.SuccessMediaType)
            "Same owner for HTTP description"

    let forbidden =
        typeof<ClaimCore.Protocol.EndpointDescription>.Assembly.GetReferencedAssemblies()
        |> Array.map _.Name

    Expect.isFalse
        (forbidden
         |> Array.exists (fun name ->
             nonNull name |> fun n -> n.StartsWith("ClaimCore.") || n = "Npgsql"))
        "Protocol assembly has no transitive server entry"

let private exactDefinition () =
    let case =
        corpus ()
        |> List.find (fun case -> case.GetProperty("id").GetString() = "valid-definition-described")

    let bytes = case.GetProperty("value").GetRawText()
    let value = decoded Client.definition.Response bytes

    Expect.equal
        value.Outcome.Data.Definition.Fields.Length
        13
        "Metadata is a useful typed list, not a fixed tuple API"

    let fields = value.Outcome.Data.Definition.Fields

    let changed =
        { value with
            Outcome =
                { value.Outcome with
                    Data =
                        { value.Outcome.Data with
                            Definition =
                                { value.Outcome.Data.Definition with
                                    Fields = List.rev fields
                                }
                        }
                }
        }

    Expect.isError
        (Client.definition.Response.Encode(maximum, changed))
        "A reordered constant definition is not accepted"

let private unsupported () =
    let projection = ContractProjection.current ()
    let first = List.head projection.WebEndpoints

    for schema in
        [
            Schema.never
            Schema.reference "Absent"
            Schema.objectOf true []
            Schema.oneOf []
            Schema.string (Some "unqualified-format") None None None
        ] do
        let changed =
            { projection with
                WebEndpoints = [ { first with Response = schema } ]
            }

        Expect.throws
            (fun () -> DotNetProtocol.artifacts changed |> ignore)
            "Unsupported schemas fail rather than erasing their type"

    let collision =
        { projection with
            WebEndpoints =
                [
                    first
                    { first with
                        Identifier = first.Identifier.Replace(".", "_")
                    }
                ]
        }

    Expect.throws
        (fun () -> DotNetProtocol.artifacts collision |> ignore)
        "Colliding generated endpoint names are refused"


let tests =
    testList
        "client protocol generation and corpus"
        [
            testCase
                "all published response alternatives agree with the shared corpus"
                corpusConformance
            testCase
                "complete descriptors retain the service contract and no server dependency"
                catalog
            testCase
                "typed semantic metadata still obeys the exact published definition"
                exactDefinition
            testCase "unsupported generation shapes fail closed" unsupported
        ]
