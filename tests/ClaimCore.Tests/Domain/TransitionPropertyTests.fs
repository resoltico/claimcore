module ClaimCore.Tests.TransitionPropertyTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.PropertyHarness

type private Step =
    { Act: UnvalidatedCaseAct; Stale: bool }

let private businessActs =
    [
        UnvalidatedCaseAct.Open registration
        UnvalidatedCaseAct.AmendRegistration
            { registration with
                ClaimantName = "Amended Synthetic Name"
            }
        UnvalidatedCaseAct.Correct
            {
                Registration =
                    RegistrationGroup.Replace
                        { registration with
                            ClaimantName = "Corrected Synthetic Name"
                        }
                Decision = DecisionGroup.Keep
                Payment = PaymentGroup.Keep
            }
        UnvalidatedCaseAct.Decide decision
        UnvalidatedCaseAct.Decide { decision with PayableAmount = "0" }
        UnvalidatedCaseAct.WithdrawDecision
        UnvalidatedCaseAct.RecordPayment { PaymentDate = "2026-08-20" }
        UnvalidatedCaseAct.ClearPayment
        UnvalidatedCaseAct.Close
        UnvalidatedCaseAct.Reopen
    ]

let private acts =
    businessActs
    @ [
        UnvalidatedCaseAct.Void "Entry error"
        UnvalidatedCaseAct.Reinstate "Entry confirmed"
        UnvalidatedCaseAct.Erase
    ]

let private stepGenerator =
    gen {
        let! act = Gen.item acts
        let! stale = Gen.bool
        return { Act = act; Stale = stale }
    }

let private initial: CaseSnapshot =
    {
        Reference = CaseFixtures.reference "UNIT-001"
        State =
            CaseState.Active(
                {
                    CaseReference = CaseFixtures.reference "UNIT-001"
                    Status = CaseStatus.Opened
                    Registration =
                        CaseFixtures.registration
                            { Inputs.registration with
                                ClaimantName = "Example Claimant Ltd"
                                InsurerName = "Example Alleged Insurer"
                                ClaimedAmount = "1000"
                            }
                    Decision = None
                    Payment = None
                }
            )
        Revision = 1L
    }

let private checkSequence steps =
    let actualInitial = opened ()

    let _, _, valid =
        steps
        |> List.fold
            (fun (current, expected, valid) step ->
                let candidate =
                    actRequest
                        (if step.Stale then
                             expected.Revision - 1L
                         else
                             expected.Revision)
                        step.Act

                let result = CaseFixtures.decide today candidate (Some current)
                let nextExpected = TransitionOracle.advance expected step.Stale step.Act
                let observed = result |> Result.map Case.snapshot
                let matches = TransitionOracle.matches nextExpected observed
                let nextActual = result |> Result.defaultValue current
                nextActual, Option.defaultValue expected nextExpected, valid && matches)
            (actualInitial, initial, Case.snapshot actualInitial = initial)

    valid

let private acceptingPath =
    [
        UnvalidatedCaseAct.Decide decision
        UnvalidatedCaseAct.RecordPayment { PaymentDate = "2026-08-20" }
        UnvalidatedCaseAct.Close
        UnvalidatedCaseAct.Reopen
        UnvalidatedCaseAct.ClearPayment
        UnvalidatedCaseAct.WithdrawDecision
        UnvalidatedCaseAct.AmendRegistration registration
        UnvalidatedCaseAct.Close
        UnvalidatedCaseAct.Close
        UnvalidatedCaseAct.Reopen
    ]
    |> List.map (fun act -> { Act = act; Stale = false })
    |> fun steps ->
        let tail =
            [
                UnvalidatedCaseAct.Void "Entry error"
                UnvalidatedCaseAct.Close
                UnvalidatedCaseAct.Reinstate "Entry confirmed"
                UnvalidatedCaseAct.Erase
                UnvalidatedCaseAct.Open registration
            ]

        steps @ (tail |> List.map (fun act -> { Act = act; Stale = false }))

let private sequenceProperty =
    let sequenceGenerator =
        Gen.list (Range.constant 1 (sequenceMaximum ())) stepGenerator

    property {
        let! steps = sequenceGenerator
        return checkSequence steps
    }

let private negativeControls () =
    let decided =
        TransitionOracle.advance initial false ((UnvalidatedCaseAct.Decide decision))
        |> Option.get

    let paid =
        TransitionOracle.advance
            decided
            false
            ((UnvalidatedCaseAct.RecordPayment { PaymentDate = "2026-08-20" }))
        |> Option.get

    let closed =
        TransitionOracle.advance paid false (UnvalidatedCaseAct.Close) |> Option.get

    let controls =
        [
            Some decided, Error Refusal.ResourceUnavailable
            Some paid,
            Ok
                { paid with
                    State = CaseState.Active({ viewFields paid with Payment = None })
                }
            Some paid,
            Ok
                { paid with
                    Revision = decided.Revision
                }
            Some closed,
            Ok
                { closed with
                    State =
                        CaseState.Active(
                            { viewFields closed with
                                Payment = None
                            }
                        )
                }
            None, Ok paid
        ]

    controls
    |> List.iter (fun (expected, actual) ->
        Expect.isFalse
            (TransitionOracle.matches expected actual)
            "Plausible defective outcome is detected")

type private ReachableState =
    | OpenUndecided
    | OpenDecided
    | OpenZeroDecision
    | OpenPaid
    | ClosedUndecided
    | ClosedDecided
    | ClosedPaid
    | Voided
    | Erased

let private reach state =
    match state with
    | OpenUndecided -> opened ()
    | OpenDecided -> decided ()
    | OpenZeroDecision ->
        opened ()
        |> apply (UnvalidatedCaseAct.Decide { decision with PayableAmount = "0" })
        |> accepted
    | OpenPaid -> paid ()
    | ClosedUndecided -> opened () |> apply UnvalidatedCaseAct.Close |> accepted
    | ClosedDecided -> decided () |> apply UnvalidatedCaseAct.Close |> accepted
    | ClosedPaid -> paid () |> apply UnvalidatedCaseAct.Close |> accepted
    | Voided ->
        CaseFixtures.decide
            today
            (actRequest 1L ((UnvalidatedCaseAct.Void "Entry error")))
            (Some(opened ()))
        |> accepted
    | Erased ->
        CaseFixtures.decide today (actRequest 1L (UnvalidatedCaseAct.Erase)) (Some(opened ()))
        |> accepted

let private expectedBusiness =
    function
    | OpenUndecided -> [ "AMEND_REGISTRATION"; "DECIDE"; "CLOSE" ]
    | OpenDecided -> [ "CORRECT"; "DECIDE"; "WITHDRAW_DECISION"; "RECORD_PAYMENT"; "CLOSE" ]
    | OpenZeroDecision -> [ "CORRECT"; "DECIDE"; "WITHDRAW_DECISION"; "CLOSE" ]
    | OpenPaid -> [ "CORRECT"; "CLEAR_PAYMENT"; "CLOSE" ]
    | ClosedUndecided
    | ClosedDecided
    | ClosedPaid -> [ "CORRECT"; "REOPEN" ]
    | Voided
    | Erased -> []

let private expectedActs state =
    match state with
    | Voided -> [ "REINSTATE"; "ERASE" ]
    | Erased -> []
    | _ -> expectedBusiness state @ [ "VOID"; "ERASE" ]

let private executionMatchesAdvertisement claim expected =
    let view = Case.snapshot claim

    acts
    |> List.forall (fun act ->
        let candidate =
            { actRequest view.Revision act with
                CaseReference = CaseReference.format view.Reference
            }

        let accepted = CaseFixtures.decide today candidate (Some claim) |> Result.isOk
        accepted = Set.contains (UnvalidatedCaseActs.name act) expected)

let private availabilityProperty =
    property {
        let! state =
            Gen.item
                [
                    OpenUndecided
                    OpenDecided
                    OpenZeroDecision
                    OpenPaid
                    ClosedUndecided
                    ClosedDecided
                    ClosedPaid
                    Voided
                    Erased
                ]

        let claim = reach state
        let expected = expectedActs state |> Set.ofList
        let actual = (Case.eligibleActs claim |> List.map CaseActKind.token) |> Set.ofList
        return actual = expected && executionMatchesAdvertisement claim expected
    }

[<Tests>]
let tests =
    testList
        "Hedgehog state invariants"
        [
            testCase "accepted and rejected sequences match complete independent state" (fun () ->
                Expect.isTrue (checkSequence acceptingPath) "Complete fixed acceptance path"
                run "case transition sequence" sequenceProperty)
            testCase
                "sequence oracle rejects refusal-only and corrupt accepted outcomes"
                negativeControls
            testCase "reachable states match the independent availability matrix" (fun () ->
                run "state act eligibility" availabilityProperty)
        ]
