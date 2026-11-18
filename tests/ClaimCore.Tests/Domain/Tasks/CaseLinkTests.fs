module ClaimCore.Tests.Tasks.CaseLinkTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Add = ClaimCore.Domain.Tasks.Link.Add
module Remove = ClaimCore.Domain.Tasks.Link.Remove

let private add number (state: CaseFamily) candidate lookup =
    Add.decide
        (context number)
        { environment with
            RelatedCase = lookup
        }
        (Some(DecisionFacts.fromReplay state))
        (Binding.LinkNew(target state))
        (inputs [ text InputTarget.LinkTargetId (CaseId.format candidate) ])

let private live =
    function
    | CaseSelector.Id id -> Some(id, Lifecycle.Active)
    | CaseSelector.Reference _ -> None

[<Tests>]
let tests =
    testList
        "related case rules"
        [
            testCase
                "self and duplicate links are refused and the twentieth link is the last allowed"
                (fun () ->
                    let initial = family ()

                    let candidate number =
                        CaseId.ofOperationId (operation (100 + number))

                    add 2 initial initial.Id live
                    |> expectRefusal (Refusal.Case CaseRefusal.SelfLink)

                    let first = add 2 initial (candidate 1) live |> accepted |> apply initial

                    add 3 first (candidate 1) live
                    |> expectRefusal (Refusal.Case CaseRefusal.DuplicateLink)

                    let full =
                        [ 2..20 ]
                        |> List.fold
                            (fun state index ->
                                add (index + 1) state (candidate index) live
                                |> accepted
                                |> apply state)
                            first

                    Expect.equal full.Links.Count 20 "Exactly 20 current links can be accepted"

                    add 22 full (candidate 21) live
                    |> expectRefusal (Refusal.Case CaseRefusal.LinkLimitReached))
            testCase
                "targets without admitted access or erased targets yield the same unavailable refusal"
                (fun () ->
                    let state = family ()
                    let target = CaseId.ofOperationId (operation 99)
                    let refusal = Refusal.Access AccessRefusal.ResourceUnavailable

                    add 2 state target (fun _ -> None) |> expectRefusal refusal

                    add 2 state target (fun _ -> Some(target, Lifecycle.Erased))
                    |> expectRefusal refusal)
        ]
