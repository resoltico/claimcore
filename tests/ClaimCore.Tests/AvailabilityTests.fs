module ClaimCore.Tests.AvailabilityTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private candidates =
    [
        Command.Open registration
        Command.AmendRegistration registration
        Command.Decide decision
        Command.WithdrawDecision
        Command.RecordPayment "2026-08-20"
        Command.ClearPayment
        Command.Close
        Command.Reopen
    ]

let private stateActions =
    testCase "[CC-DOM-002] each state advertises and enforces the specified command set" (fun () ->
        let zero =
            opened ()
            |> apply (Command.Decide { decision with PayableAmount = "0" })
            |> accepted

        let cases =
            [
                (opened (), [ "AMEND_REGISTRATION"; "DECIDE"; "CLOSE" ])
                (decided (), [ "DECIDE"; "WITHDRAW_DECISION"; "RECORD_PAYMENT"; "CLOSE" ])
                (paid (), [ "CLEAR_PAYMENT"; "CLOSE" ])
                (zero, [ "DECIDE"; "WITHDRAW_DECISION"; "CLOSE" ])
            ]

        for (state, expected) in cases do
            let actions = Claim.availableCommands state |> Set.ofList

            Expect.equal
                actions
                (Set.ofList expected)
                "Advertised actions match the domain contract"

            candidates
            |> List.iter (fun command ->
                let result = apply command state

                Expect.equal
                    (Result.isOk result)
                    (Set.contains (Commands.name command) actions)
                    "No separately invented UI state machine")

            let closed = apply Command.Close state |> accepted

            Expect.equal
                (Claim.availableCommands closed)
                [ "REOPEN" ]
                "Closed cases can only reopen")

let private edgeActions =
    testList
        "availability edge conditions"
        [
            testCase "no action is advertised at the exhausted revision" (fun () ->
                let state = Claim.view (opened ())

                let exhausted =
                    Claim.restore
                        { state with
                            Version = Int64.MaxValue - 1L
                        }
                    |> accepted

                Expect.isEmpty
                    (Claim.availableCommands exhausted)
                    "No action can produce an unrepresentable next revision")
            testCase "availability does not substitute for payload validation" (fun () ->
                let state = opened ()
                Expect.contains (Claim.availableCommands state) "DECIDE" "State eligibility"

                let invalid =
                    Command.Decide
                        { decision with
                            PaymentDecisionDate = "2027-01-01"
                        }

                Expect.isTrue
                    (apply invalid state |> isInvalid)
                    "Actual execution rechecks future date")
        ]

let tests =
    testList "state guard and advertised actions" [ stateActions; edgeActions ]
