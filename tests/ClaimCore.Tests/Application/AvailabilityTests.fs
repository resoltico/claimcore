module ClaimCore.Tests.AvailabilityTests

open ClaimCore.Wire
open ClaimCore.Presentation

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private availableActs record =
    Case.eligibleActs record |> List.map CaseActKind.token

let private candidates = TransitionOracle.samples |> List.map fst

let private stateActions =
    testCase "each state advertises and enforces the specified act set" (fun () ->
        let zero =
            opened ()
            |> apply (
                AuthoredCaseAct.Decide(
                    ClaimCore.TestSupport.Inputs.decisionWith
                        [ BusinessField.PayableAmount, ("0") ]
                        (decision)
                )
            )
            |> accepted

        for state in [ opened (); decided (); paid (); zero ] do
            let expected = TransitionOracle.offered (Case.snapshot state).State |> Set.ofList
            let actions = availableActs state |> Set.ofList
            Expect.equal actions expected "Advertised actions match the independent oracle"

            for act in candidates do
                let result =
                    match act with
                    | AuthoredCaseAct.RemovePayment id ->
                        let snapshot = Case.snapshot state

                        let fields =
                            match snapshot.State with
                            | CaseState.Active fields -> fields
                            | _ -> failtest "Active fixed witness required"

                        let selected =
                            if fields.Finance.PaymentCount = 0L then
                                None
                            else
                                Expect.equal
                                    fields.Finance.PaymentCount
                                    1L
                                    "This availability fixture owns exactly one active payment"

                                PaymentEntry.create
                                    (OperationId.create id |> Option.get)
                                    (fields.Finance.PaymentDates |> Option.get).Earliest
                                    fields.Finance.PaidAmount
                                |> Fixtures.accepted
                                |> Some

                        applyWith
                            ((ClaimCore.TestSupport.CaseFixtures.paymentRows
                                (snapshot.CaseId)
                                (Option.toList (selected))))
                            act
                            state
                    | _ -> apply act state

                Expect.equal
                    (result |> Result.isOk)
                    (TransitionOracle.allows (Some((Case.snapshot state).State)) act)
                    ("Execution and advertisement follow the oracle for "
                     + CaseActKind.token (CaseAct.kind act))

            let closed = apply (AuthoredCaseAct.Bare BareAct.Close) state |> accepted

            Expect.equal
                (availableActs closed)
                (TransitionOracle.offered (Case.snapshot closed).State)
                "Closed case advertisements follow the oracle")

let private edgeActions =
    testList
        "availability edge conditions"
        [
            testCase "availability does not substitute for payload validation" (fun () ->
                let state = opened ()
                Expect.contains (availableActs state) "case.decide" "State eligibility"

                let invalid =
                    AuthoredCaseAct.Decide(
                        ClaimCore.TestSupport.Inputs.decisionWith
                            [ BusinessField.PaymentDecisionDate, ("2027-01-01") ]
                            (decision)
                    )

                Expect.isTrue
                    (apply invalid state |> isInvalid)
                    "Actual execution rechecks future date")
        ]

[<Tests>]
let tests =
    testList "state guard and advertised actions" [ stateActions; edgeActions ]
