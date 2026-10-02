module ClaimCore.Tests.TransitionPropertyTests

open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.PropertyHarness

type private Step = { Command: Command; Stale: bool }

let private commands =
    [
        Command.Open registration
        Command.AmendRegistration
            { registration with
                ClaimantName = "Amended Synthetic Name"
            }
        Command.CorrectCase
            {
                Registration =
                    RegistrationCorrection.Replace
                        { registration with
                            ClaimantName = "Corrected Synthetic Name"
                        }
                Decision = DecisionCorrection.Keep
                Payment = PaymentCorrection.Keep
            }
        Command.Decide decision
        Command.Decide { decision with PayableAmount = "0" }
        Command.WithdrawDecision
        Command.RecordPayment "2026-08-20"
        Command.ClearPayment
        Command.Close
        Command.Reopen
    ]

let private stepGenerator =
    gen {
        let! command = Gen.item commands
        let! stale = Gen.bool
        return { Command = command; Stale = stale }
    }

let private initial: CaseView =
    {
        Fields =
            {
                IncidentDate = "2026-08-01"
                IncidentNotificationDate = "2026-08-03"
                IncidentCountry = "Lithuania"
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
            }
        Version = 1L
    }

let private checkSequence steps =
    let actualInitial = opened ()

    let _, _, valid =
        steps
        |> List.fold
            (fun (current, expected, valid) step ->
                let candidate =
                    request
                        (if step.Stale then
                             expected.Version - 1L
                         else
                             expected.Version)
                        step.Command

                let result = Claim.decide today candidate (Some current)
                let nextExpected = TransitionOracle.advance expected step.Stale step.Command
                let observed = result |> Result.map Claim.view
                let matches = TransitionOracle.matches nextExpected observed
                let nextActual = result |> Result.defaultValue current
                nextActual, Option.defaultValue expected nextExpected, valid && matches)
            (actualInitial, initial, Claim.view actualInitial = initial)

    valid

let private acceptingPath =
    [
        Command.Decide decision
        Command.RecordPayment "2026-08-20"
        Command.Close
        Command.Reopen
        Command.ClearPayment
        Command.WithdrawDecision
        Command.AmendRegistration registration
        Command.Close
        Command.Close
        Command.Reopen
    ]
    |> List.map (fun command -> { Command = command; Stale = false })

let private sequenceProperty =
    let sequenceGenerator =
        Gen.list (Range.constant 1 (sequenceMaximum ())) stepGenerator

    property {
        let! steps = sequenceGenerator
        return checkSequence acceptingPath && checkSequence steps
    }

let private negativeControls () =
    let decided =
        TransitionOracle.advance initial false (Command.Decide decision) |> Option.get

    let paid =
        TransitionOracle.advance decided false (Command.RecordPayment "2026-08-20")
        |> Option.get

    let closed = TransitionOracle.advance paid false Command.Close |> Option.get

    let controls =
        [
            Some decided, Error DomainError.NotFound
            Some paid,
            Ok
                { paid with
                    Fields = { paid.Fields with PaymentDate = None }
                }
            Some paid, Ok { paid with Version = decided.Version }
            Some closed,
            Ok
                { closed with
                    Fields =
                        { closed.Fields with
                            PaymentDate = None
                        }
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

let private reach state =
    match state with
    | OpenUndecided -> opened ()
    | OpenDecided -> decided ()
    | OpenZeroDecision ->
        opened ()
        |> apply (Command.Decide { decision with PayableAmount = "0" })
        |> accepted
    | OpenPaid -> paid ()
    | ClosedUndecided -> opened () |> apply Command.Close |> accepted
    | ClosedDecided -> decided () |> apply Command.Close |> accepted
    | ClosedPaid -> paid () |> apply Command.Close |> accepted

let private expectedCommands =
    function
    | OpenUndecided -> [ "AMEND_REGISTRATION"; "DECIDE"; "CLOSE" ]
    | OpenDecided -> [ "CORRECT_CASE"; "DECIDE"; "WITHDRAW_DECISION"; "RECORD_PAYMENT"; "CLOSE" ]
    | OpenZeroDecision -> [ "CORRECT_CASE"; "DECIDE"; "WITHDRAW_DECISION"; "CLOSE" ]
    | OpenPaid -> [ "CORRECT_CASE"; "CLEAR_PAYMENT"; "CLOSE" ]
    | ClosedUndecided
    | ClosedDecided
    | ClosedPaid -> [ "CORRECT_CASE"; "REOPEN" ]

let private executionMatchesAdvertisement claim expected =
    let view = Claim.view claim

    commands
    |> List.forall (fun command ->
        let candidate =
            { request view.Version command with
                CaseReference = view.Fields.CaseReference
            }

        let accepted = Claim.decide today candidate (Some claim) |> Result.isOk
        accepted = Set.contains (Commands.name command) expected)

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
                ]

        let claim = reach state
        let expected = expectedCommands state |> Set.ofList
        let actual = Claim.availableCommands claim |> Set.ofList
        return actual = expected && executionMatchesAdvertisement claim expected
    }

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
