module ClaimCore.Tests.TransitionPropertyTests

open ClaimCore.Contract

open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.PropertyHarness

type private Step = { Act: CaseAct; Stale: bool }

let private businessCommands =
    [
        BusinessCommand.Open registration
        BusinessCommand.AmendRegistration
            { registration with
                ClaimantName = "Amended Synthetic Name"
            }
        BusinessCommand.CorrectCase
            {
                Registration =
                    RegistrationCorrection.Replace
                        { registration with
                            ClaimantName = "Corrected Synthetic Name"
                        }
                Decision = DecisionCorrection.Keep
                Payment = PaymentCorrection.Keep
            }
        BusinessCommand.Decide decision
        BusinessCommand.Decide { decision with PayableAmount = "0" }
        BusinessCommand.WithdrawDecision
        BusinessCommand.RecordPayment "2026-08-20"
        BusinessCommand.ClearPayment
        BusinessCommand.Close
        BusinessCommand.Reopen
    ]

let private commands =
    (businessCommands |> List.map CaseAct.Business)
    @ [
        CaseAct.Stewardship(StewardshipAct.Void "Entry error")
        CaseAct.Stewardship(StewardshipAct.Reinstate "Entry confirmed")
        CaseAct.Stewardship StewardshipAct.Erase
    ]

let private stepGenerator =
    gen {
        let! command = Gen.item commands
        let! stale = Gen.bool
        return { Act = command; Stale = stale }
    }

let private initial: CaseSnapshot =
    {
        Reference = "UNIT-001"
        View =
            CaseView.Live(
                {
                    IncidentDate = "2026-08-01"
                    IncidentNotificationDate = "2026-08-03"
                    IncidentCountry = "LV"
                    ClaimantName = "Example Claimant Ltd"
                    InsurerName = "Example Alleged Insurer"
                    ClaimedAmount = "1000"
                    ClaimedCurrency = "EUR"
                    CaseReference = "UNIT-001"
                    PaymentDecisionDate = None
                    PayableAmount = None
                    PayableCurrency = None
                    PaymentDate = None
                    Status = CaseStatus.Opened
                },
                Disposition.Active
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

                let result = Case.decide today candidate (Some current)
                let nextExpected = TransitionOracle.advance expected step.Stale step.Act
                let observed = result |> Result.map Case.snapshot
                let matches = TransitionOracle.matches nextExpected observed
                let nextActual = result |> Result.defaultValue current
                nextActual, Option.defaultValue expected nextExpected, valid && matches)
            (actualInitial, initial, Case.snapshot actualInitial = initial)

    valid

let private acceptingPath =
    [
        BusinessCommand.Decide decision
        BusinessCommand.RecordPayment "2026-08-20"
        BusinessCommand.Close
        BusinessCommand.Reopen
        BusinessCommand.ClearPayment
        BusinessCommand.WithdrawDecision
        BusinessCommand.AmendRegistration registration
        BusinessCommand.Close
        BusinessCommand.Close
        BusinessCommand.Reopen
    ]
    |> List.map (fun command ->
        {
            Act = CaseAct.Business command
            Stale = false
        })
    |> fun steps ->
        let tail =
            [
                CaseAct.Stewardship(StewardshipAct.Void "Entry error")
                CaseAct.Business BusinessCommand.Close
                CaseAct.Stewardship(StewardshipAct.Reinstate "Entry confirmed")
                CaseAct.Stewardship StewardshipAct.Erase
                CaseAct.Business(BusinessCommand.Open registration)
            ]

        steps @ (tail |> List.map (fun act -> { Act = act; Stale = false }))

let private sequenceProperty =
    let sequenceGenerator =
        Gen.list (Range.constant 1 (sequenceMaximum ())) stepGenerator

    property {
        let! steps = sequenceGenerator
        return checkSequence acceptingPath && checkSequence steps
    }

let private negativeControls () =
    let decided =
        TransitionOracle.advance initial false (CaseAct.Business(BusinessCommand.Decide decision))
        |> Option.get

    let paid =
        TransitionOracle.advance
            decided
            false
            (CaseAct.Business(BusinessCommand.RecordPayment "2026-08-20"))
        |> Option.get

    let closed =
        TransitionOracle.advance paid false (CaseAct.Business BusinessCommand.Close)
        |> Option.get

    let controls =
        [
            Some decided, Error CaseRefusal.NotFound
            Some paid,
            Ok
                { paid with
                    View =
                        CaseView.Live(
                            { viewFields paid with
                                PaymentDate = None
                            },
                            Disposition.Active
                        )
                }
            Some paid,
            Ok
                { paid with
                    Revision = decided.Revision
                }
            Some closed,
            Ok
                { closed with
                    View =
                        CaseView.Live(
                            { viewFields closed with
                                PaymentDate = None
                            },
                            Disposition.Active
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
        |> apply (BusinessCommand.Decide { decision with PayableAmount = "0" })
        |> accepted
    | OpenPaid -> paid ()
    | ClosedUndecided -> opened () |> apply BusinessCommand.Close |> accepted
    | ClosedDecided -> decided () |> apply BusinessCommand.Close |> accepted
    | ClosedPaid -> paid () |> apply BusinessCommand.Close |> accepted
    | Voided ->
        Case.decide
            today
            (actRequest 1L (CaseAct.Stewardship(StewardshipAct.Void "Entry error")))
            (Some(opened ()))
        |> accepted
    | Erased ->
        Case.decide
            today
            (actRequest 1L (CaseAct.Stewardship StewardshipAct.Erase))
            (Some(opened ()))
        |> accepted

let private expectedBusiness =
    function
    | OpenUndecided -> [ "AMEND_REGISTRATION"; "DECIDE"; "CLOSE" ]
    | OpenDecided -> [ "CORRECT_CASE"; "DECIDE"; "WITHDRAW_DECISION"; "RECORD_PAYMENT"; "CLOSE" ]
    | OpenZeroDecision -> [ "CORRECT_CASE"; "DECIDE"; "WITHDRAW_DECISION"; "CLOSE" ]
    | OpenPaid -> [ "CORRECT_CASE"; "CLEAR_PAYMENT"; "CLOSE" ]
    | ClosedUndecided
    | ClosedDecided
    | ClosedPaid -> [ "CORRECT_CASE"; "REOPEN" ]
    | Voided
    | Erased -> []

let private expectedCommands state =
    match state with
    | Voided -> [ "REINSTATE"; "ERASE" ]
    | Erased -> []
    | _ -> expectedBusiness state @ [ "VOID"; "ERASE" ]

let private executionMatchesAdvertisement claim expected =
    let view = Case.snapshot claim

    commands
    |> List.forall (fun command ->
        let candidate =
            { actRequest view.Revision command with
                CaseReference = view.Reference
            }

        let accepted = Case.decide today candidate (Some claim) |> Result.isOk
        accepted = Set.contains (CaseActs.name command) expected)

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
        let expected = expectedCommands state |> Set.ofList
        let actual = (Case.eligibleActs claim |> List.map CaseActKinds.token) |> Set.ofList
        return actual = expected && executionMatchesAdvertisement claim expected
    }

[<Tests>]
let tests =
    testList
        "Hedgehog state invariants"
        [
            testCase "accepted and rejected sequences match complete independent state" (fun () ->
                run "CC-PROP-TRANSITION-001" sequenceProperty)
            testCase
                "sequence oracle rejects refusal-only and corrupt accepted outcomes"
                negativeControls
            testCase "reachable states match the independent availability matrix" (fun () ->
                run "CC-PROP-AVAILABILITY-001" availabilityProperty)
        ]
