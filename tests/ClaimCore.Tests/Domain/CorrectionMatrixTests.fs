module ClaimCore.Tests.CorrectionMatrixTests

open ClaimCore.TestSupport
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private registrations =
    [
        Amendment.Keep, false
        Amendment.Update(
            Inputs.registrationUpdate (
                Inputs.registrationWith
                    [ BusinessField.ClaimantName, "Synthetic matrix party" ]
                    registration
            )
        ),
        true
    ]

let private decisions =
    [
        Change.Keep, "KEEP"
        Change.Update(
            Inputs.decisionUpdate (
                Inputs.decisionWith [ BusinessField.PayableAmount, "800" ] decision
            )
        ),
        "UPDATE"
        Change.Clear, "CLEAR"
    ]

[<Tests>]
let tests =
    testList
        "correction group invariants"
        [
            testCase
                "two correction groups follow independent open and closed financial-state cases"
                (fun () ->
                    let zero =
                        opened ()
                        |> apply (
                            AuthoredCaseAct.Decide(
                                Inputs.decisionWith [ BusinessField.PayableAmount, "0" ] decision
                            )
                        )
                        |> accepted

                    let partial =
                        decided ()
                        |> apply (
                            AuthoredCaseAct.RecordPayment(
                                Inputs.paymentFrom [ BusinessField.PaidAmount, "250" ]
                            )
                        )
                        |> accepted

                    let states =
                        [
                            opened (), false, false
                            decided (), true, false
                            zero, true, false
                            partial, true, true
                            paid (), true, true
                        ]

                    for original, hasDecision, hasEntries in states do
                        for closed in [ false; true ] do
                            let before =
                                if closed then
                                    original
                                    |> apply (AuthoredCaseAct.Bare BareAct.Close)
                                    |> accepted
                                else
                                    original

                            let snapshot = Case.snapshot before
                            let prior = viewFields snapshot

                            for registration, registrationChanged in registrations do
                                for decision, mode in decisions do
                                    let permitted =
                                        (closed || hasDecision)
                                        && (mode = "KEEP" || hasDecision)
                                        && (mode <> "CLEAR" || not hasEntries)
                                        && (registrationChanged || mode <> "KEEP")

                                    let result =
                                        before
                                        |> apply (
                                            AuthoredCaseAct.Correct
                                                {
                                                    Registration = registration
                                                    Decision = decision
                                                }
                                        )

                                    Expect.equal
                                        (Result.isOk result)
                                        permitted
                                        "Requirements determine whether this correction is meaningful and preserves payments"

                                    match result with
                                    | Error _ ->
                                        Expect.equal
                                            (Case.snapshot before)
                                            snapshot
                                            "Refusal cannot mutate accepted facts"
                                    | Ok record ->
                                        let after = Case.snapshot record
                                        let current = viewFields after

                                        Expect.equal
                                            current.Status
                                            prior.Status
                                            "Status is preserved"

                                        Expect.equal
                                            current.CaseReference
                                            prior.CaseReference
                                            "Target is preserved"

                                        Expect.equal
                                            current.Payments
                                            prior.Payments
                                            "Every payment entry is preserved"

                                        Expect.equal
                                            (Revision.value after.Revision)
                                            (Revision.value snapshot.Revision + 1L)
                                            "One accepted revision"

                                        let claimant =
                                            if registrationChanged then
                                                "Synthetic matrix party"
                                            else
                                                PartyName.format (
                                                    Registration.claimant prior.Registration
                                                )

                                        Expect.equal
                                            (PartyName.format (
                                                Registration.claimant current.Registration
                                            ))
                                            claimant
                                            "Only the asserted claim fact changes"

                                        match mode with
                                        | "UPDATE" ->
                                            Expect.equal
                                                (current.Decision
                                                 |> Option.map (Decision.payable >> Money.value))
                                                (Some 800M)
                                                "Explicit decision amount"

                                            Expect.equal
                                                (CaseProgress.ofPayment current.Progress)
                                                (if hasEntries then
                                                     CaseProgress.PartlyPaid
                                                 else
                                                     CaseProgress.Decided)
                                                "The retained sum determines financial progress"
                                        | "CLEAR" ->
                                            Expect.equal
                                                current.Progress
                                                PaymentProgress.Undecided
                                                "Only an entry-free decision is cleared"
                                        | _ ->
                                            Expect.equal
                                                current.Progress
                                                prior.Progress
                                                "Keep preserves financial state")
            testCase
                "correcting paid payable to zero is refused without removing entries"
                (fun () ->
                    let before = paid ()

                    let act =
                        AuthoredCaseAct.Correct
                            {
                                Registration = Amendment.Keep
                                Decision =
                                    Change.Update(
                                        Inputs.decisionUpdate (
                                            Inputs.decisionWith
                                                [ BusinessField.PayableAmount, "0" ]
                                                decision
                                        )
                                    )
                            }

                    Expect.isError
                        (before |> apply act)
                        "A correction cannot erase actual paid entries")
        ]
