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

[<Tests>]
let coverageTests =
    testCase
        "independent identity evidence rejects every omitted comparison and required scope"
        (fun () ->
            let rules = CorrelationBindings.all

            let replace target replacement =
                rules |> List.map (fun rule -> if rule = target then replacement else rule)

            for rule in rules do
                if not (List.isEmpty rule.Bindings) then
                    Expect.throws
                        (fun () ->
                            CorrelationValidation.validate (
                                replace rule { rule with Bindings = [] }
                            ))
                        "All identity checks cannot disappear"

                for index in 0 .. rule.Bindings.Length - 1 do
                    let missing =
                        rule.Bindings
                        |> List.indexed
                        |> List.choose (fun (position, binding) ->
                            if position = index then None else Some binding)

                    Expect.throws
                        (fun () ->
                            CorrelationValidation.validate (
                                replace rule { rule with Bindings = missing }
                            ))
                        "Each required comparison has independent rejecting evidence"

                    let wrong =
                        rule.Bindings
                        |> List.mapi (fun position binding ->
                            if position <> index then
                                binding
                            else
                                { binding with
                                    Comparison =
                                        if binding.Comparison = CorrelationComparison.Exact then
                                            CorrelationComparison.AsciiReference
                                        else
                                            CorrelationComparison.Exact
                                })

                    Expect.throws
                        (fun () ->
                            CorrelationValidation.validate (
                                replace rule { rule with Bindings = wrong }
                            ))
                        "Inappropriate comparison cannot replace the required one"

                if rule.Scope.IsSome then
                    Expect.throws
                        (fun () ->
                            CorrelationValidation.validate (
                                replace rule { rule with Scope = None }
                            ))
                        "Required scope cannot disappear")
