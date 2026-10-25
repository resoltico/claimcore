module ClaimCore.Tests.AvailabilityTests

open ClaimCore.Contract

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private availableActs record =
    Case.eligibleActs record |> List.map CaseActKinds.token

let private candidates =
    [
        CaseAct.Open registration
        CaseAct.AmendRegistration registration
        CaseAct.Correct
            {
                Registration =
                    RegistrationGroup.Replace
                        { registration with
                            ClaimantName = "Corrected Synthetic Claimant"
                        }
                Decision = DecisionGroup.Keep
                Payment = PaymentGroup.Keep
            }
        CaseAct.Decide decision
        CaseAct.WithdrawDecision
        CaseAct.RecordPayment { PaymentDate = "2026-08-20" }
        CaseAct.ClearPayment
        CaseAct.Close
        CaseAct.Reopen
    ]

let private stateActions =
    testCase "[CC-DOM-002] each state advertises and enforces the specified command set" (fun () ->
        let zero =
            opened ()
            |> apply (CaseAct.Decide { decision with PayableAmount = "0" })
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
            |> List.iter (fun command ->
                let result = apply command state

                Expect.equal
                    (Result.isOk result)
                    (Set.contains (CaseActs.name command) actions)
                    "No separately invented UI state machine")

            let closed = apply CaseAct.Close state |> accepted

            Expect.equal
                (availableActs closed)
                [ "CORRECT"; "REOPEN"; "VOID"; "ERASE" ]
                "Closed cases can correct facts or reopen")

let private edgeActions =
    testList
        "availability edge conditions"
        [
            testCase "no action is advertised at the exhausted revision" (fun () ->
                let state = Case.snapshot (opened ())

                let exhausted =
                    Case.ofAccepted
                        { state with
                            Revision = Revision.Maximum
                        }
                    |> accepted

                Expect.isEmpty
                    (availableActs exhausted)
                    "No action can produce an unrepresentable next revision")
            testCase "availability does not substitute for payload validation" (fun () ->
                let state = opened ()
                Expect.contains (availableActs state) "DECIDE" "State eligibility"

                let invalid =
                    CaseAct.Decide
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
