module ClaimCore.Tests.RepresentationCoverageTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private memberValue name value =
    JsonValue.tryMember name value |> Option.defaultValue JsonValue.Null

let private text =
    function
    | JsonValue.String value -> value
    | _ -> failtest "Expected declared discriminator"

[<Tests>]
let tests =
    testList
        "exported endpoint representation coverage"
        [
            testCase
                "all twenty-one endpoints have nonempty admitted behavioral envelopes and complete outcome discovery"
                (fun () ->
                    Expect.equal Api.all.Length 21 "Adopted endpoint inventory"

                    for endpoint in Api.all do
                        let samples = ResponseExamples.samples endpoint.Token

                        Expect.isNonEmpty
                            samples
                            ("Endpoint has no replacement witness: " + endpoint.Token)

                        let values =
                            samples
                            |> List.map (fun bytes ->
                                let value =
                                    StrictJson.read Limits.MaximumResponseBytes bytes
                                    |> Result.defaultWith (fun error ->
                                        failtestf "%s: %A" endpoint.Token error)

                                Expect.isOk
                                    (endpoint.ValidateOutput value)
                                    ("Actual output boundary: " + endpoint.Token)

                                Expect.isOk
                                    (endpoint.ReadOutput value)
                                    ("Actual typed reader: " + endpoint.Token)

                                value)

                        match endpoint.Id with
                        | EndpointId.Host _ ->
                            let outcomes =
                                values
                                |> List.map (memberValue "outcome" >> memberValue "kind" >> text)
                                |> Set.ofList

                            Expect.equal
                                outcomes
                                (set [ "SUCCEEDED"; "REJECTED"; "FAILED" ])
                                "Fixed authentication transport outcomes are covered"
                        | EndpointId.Actor actor ->
                            let expected =
                                match actor with
                                | ActorEndpoint.Operation OperationEndpoint.Preview ->
                                    set [ "REVIEWED"; "ALREADY_ACCEPTED"; "REJECTED"; "FAILED" ]
                                | ActorEndpoint.Operation OperationEndpoint.Execute ->
                                    set
                                        [
                                            "ACCEPTED"
                                            "ALREADY_ACCEPTED"
                                            "REJECTED"
                                            "FAILED"
                                            "OUTCOME_UNKNOWN"
                                        ]
                                | ActorEndpoint.Query QueryTaskId.OperationGet ->
                                    set
                                        [
                                            "ACCEPTED"
                                            "PENDING"
                                            "NOT_ACCEPTED"
                                            "REJECTED"
                                            "FAILED"
                                        ]
                                | ActorEndpoint.Query _
                                | ActorEndpoint.Command _ ->
                                    set [ "SUCCEEDED"; "REJECTED"; "FAILED" ]

                            for representation in [ "HUMAN"; "DATA" ] do
                                let selected =
                                    values
                                    |> List.filter (fun value ->
                                        memberValue "representation" value =
                                            JsonValue.String representation)

                                Expect.isNonEmpty
                                    selected
                                    ("Missing representation: " + endpoint.Token)

                                let kinds =
                                    selected
                                    |> List.map (
                                        memberValue "outcome" >> memberValue "kind" >> text
                                    )
                                    |> Set.ofList

                                Expect.equal
                                    kinds
                                    expected
                                    ("Authored outcome inventory: "
                                     + endpoint.Token
                                     + " "
                                     + representation)

                                if representation = "DATA" then
                                    for value in selected do
                                        let outcome = memberValue "outcome" value

                                        Expect.equal
                                            (memberValue "document" outcome)
                                            JsonValue.Null
                                            "Machine results carry no human document"

                                        Expect.equal
                                            (memberValue "view" outcome)
                                            JsonValue.Null
                                            "Machine results carry no legacy optional Screen")
        ]
