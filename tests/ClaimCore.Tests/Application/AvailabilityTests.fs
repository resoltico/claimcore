module ClaimCore.Tests.AvailabilityTests

open ClaimCore.Protocol

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private availableActs record =
    Case.eligibleActs record |> List.map CaseActKind.token

let private candidates =
    [
        AuthoredCaseAct.Open registration
        AuthoredCaseAct.AmendRegistration registration
        AuthoredCaseAct.Correct
            {
                Registration =
                    Amendment.Replace
                        { registration with
                            ClaimantName = "Corrected Synthetic Claimant"
                        }
                Decision = Change.Keep
                Payment = Change.Keep
            }
        AuthoredCaseAct.Decide decision
        AuthoredCaseAct.WithdrawDecision
        AuthoredCaseAct.RecordPayment { PaymentDate = "2026-08-20" }
        AuthoredCaseAct.ClearPayment
        AuthoredCaseAct.Close
        AuthoredCaseAct.Reopen
    ]

let private stateActions =
    testCase "each state advertises and enforces the specified act set" (fun () ->
        let zero =
            opened ()
            |> apply (AuthoredCaseAct.Decide { decision with PayableAmount = "0" })
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
                    (Set.contains (CaseActKind.token (CaseAct.kind act)) actions)
                    "No separately invented UI state machine")

            let closed = apply AuthoredCaseAct.Close state |> accepted

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
                    AuthoredCaseAct.Decide
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
