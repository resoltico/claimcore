module ClaimCore.Tests.AvailabilityTests

open ClaimCore.Protocol

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private availableActs record =
    Case.eligibleActs record |> List.map CaseActKinds.token

let private candidates =
    [
        UnvalidatedCaseAct.Open registration
        UnvalidatedCaseAct.AmendRegistration registration
        UnvalidatedCaseAct.Correct
            {
                Registration =
                    RegistrationGroup.Replace
                        { registration with
                            ClaimantName = "Corrected Synthetic Claimant"
                        }
                Decision = DecisionGroup.Keep
                Payment = PaymentGroup.Keep
            }
        UnvalidatedCaseAct.Decide decision
        UnvalidatedCaseAct.WithdrawDecision
        UnvalidatedCaseAct.RecordPayment { PaymentDate = "2026-08-20" }
        UnvalidatedCaseAct.ClearPayment
        UnvalidatedCaseAct.Close
        UnvalidatedCaseAct.Reopen
    ]

let private stateActions =
    testCase "each state advertises and enforces the specified act set" (fun () ->
        let zero =
            opened ()
            |> apply (UnvalidatedCaseAct.Decide { decision with PayableAmount = "0" })
            |> accepted

        let cases =
            [
                (opened (), [ "AMEND_REGISTRATION"; "DECIDE"; "CLOSE" ])
                (decided (),
                 [ "CORRECT"; "DECIDE"; "WITHDRAW_DECISION"; "RECORD_PAYMENT"; "CLOSE" ])
                (paid (), [ "CORRECT"; "CLEAR_PAYMENT"; "CLOSE" ])
                (zero, [ "CORRECT"; "DECIDE"; "WITHDRAW_DECISION"; "CLOSE" ])
            ]

        for (state, expected) in cases do
            let actions = availableActs state |> Set.ofList

            Expect.equal
                actions
                (Set.ofList (expected @ [ "VOID"; "ERASE" ]))
                "Advertised actions match the domain contract"

            candidates
            |> List.iter (fun act ->
                let result = apply act state

                Expect.equal
                    (Result.isOk result)
                    (Set.contains (UnvalidatedCaseActs.name act) actions)
                    "No separately invented UI state machine")

            let closed = apply UnvalidatedCaseAct.Close state |> accepted

            Expect.equal
                (availableActs closed)
                [ "CORRECT"; "REOPEN"; "VOID"; "ERASE" ]
                "Closed cases can correct facts or reopen")

let private edgeActions =
    testList
        "availability edge conditions"
        [
            testCase "availability does not substitute for payload validation" (fun () ->
                let state = opened ()
                Expect.contains (availableActs state) "DECIDE" "State eligibility"

                let invalid =
                    UnvalidatedCaseAct.Decide
                        { decision with
                            PaymentDecisionDate = "2027-01-01"
                        }

                Expect.isTrue
                    (apply invalid state |> isInvalid)
                    "Actual execution rechecks future date")
        ]

[<Tests>]
let tests =
    testList "state guard and advertised actions" [ stateActions; edgeActions ]
