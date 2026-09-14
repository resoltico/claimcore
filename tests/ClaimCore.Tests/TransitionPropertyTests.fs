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

let private registrationProjection (fields: CaseFields) =
    fields.IncidentDate,
    fields.IncidentNotificationDate,
    fields.IncidentCountry,
    fields.ClaimantName,
    fields.InsurerName,
    fields.ClaimedAmount,
    fields.ClaimedCurrency,
    fields.CaseReference

let private applyStep current step =
    let before = Claim.view current

    let expectedVersion = if step.Stale then before.Version - 1L else before.Version

    let candidate =
        { request expectedVersion step.Command with
            CaseReference = before.Fields.CaseReference
        }

    match Claim.decide today candidate (Some current) with
    | Ok changed ->
        let after = Claim.view changed

        let stable =
            match step.Command with
            | Command.AmendRegistration _
            | Command.CorrectCase _ -> after.Fields.CaseReference = before.Fields.CaseReference
            | _ -> registrationProjection after.Fields = registrationProjection before.Fields

        changed, after.Version = before.Version + 1L && stable
    | Error _ -> current, Claim.view current = before

let private explicitAcceptRejectInvariant () =
    let initial = opened ()

    let closed, acceptedInvariant =
        applyStep
            initial
            {
                Command = Command.Close
                Stale = false
            }

    let unchanged, rejectedInvariant =
        applyStep
            closed
            {
                Command = Command.Close
                Stale = false
            }

    acceptedInvariant
    && rejectedInvariant
    && Claim.view unchanged = Claim.view closed

let private sequenceProperty =
    let sequenceGenerator =
        Gen.list (Range.constant 1 (sequenceMaximum ())) stepGenerator

    property {
        let! steps = sequenceGenerator

        let _, valid =
            steps
            |> List.fold
                (fun (state, allValid) step ->
                    let next, invariant = applyStep state step
                    next, allValid && invariant)
                (opened (), true)

        return explicitAcceptRejectInvariant () && valid
    }

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
            testCase "accepted and rejected sequences preserve revision invariants" (fun () ->
                run "CC-PROP-TRANSITION-001" sequenceProperty)
            testCase "reachable states match the independent availability matrix" (fun () ->
                run "CC-PROP-AVAILABILITY-001" availabilityProperty)
        ]
