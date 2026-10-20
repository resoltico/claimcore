module ClaimCore.Tests.CorrelationSpecificationTests

open Expecto
open ClaimCore.Wire
open ClaimCore.ContractGeneration

[<Tests>]
let tests =
    testCase
        "correlation generation rejects absent, duplicate and contradictory bindings"
        (fun () ->
            let rules = CorrelationBindings.all

            let target =
                rules
                |> List.find (fun rule ->
                    rule.Endpoint = Endpoint.CaseExecute && rule.Outcome = "ACCEPTED")

            let binding = List.head target.Bindings

            let replace replacement =
                rules |> List.map (fun rule -> if rule = target then replacement else rule)

            let invalidPath =
                { target with
                    Bindings =
                        [
                            { binding with
                                RequestPath = [ "missing" ]
                            }
                        ]
                }

            for changed in
                [
                    List.tail rules
                    rules @ [ List.head rules ]
                    replace invalidPath
                    replace { target with Scope = Some "UNKNOWN" }
                ] do
                Expect.throws
                    (fun () -> CorrelationValidation.validate changed)
                    "Invalid core specification must refuse publication")
