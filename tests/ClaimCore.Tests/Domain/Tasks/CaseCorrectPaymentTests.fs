module ClaimCore.Tests.Tasks.CaseCorrectPaymentTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Tests.TaskBuilders

module Correct = ClaimCore.Domain.Tasks.Case.CorrectPayment

let private correct number selected date amount state =
    Correct.decide
        (context number)
        environment
        (Some state)
        (Binding.CasePayment(target state, state.Case.Revision, operation selected))
        (paymentInputs date amount "EUR")

[<Tests>]
let tests =
    testList
        "task case.correctPayment"
        [
            testCase
                "a fully paid open case is corrected atomically with both immutable entry identities"
                (fun () ->
                    let state = decided () |> pay 3 "2026-02-02" "1000"
                    let changes = correct 4 3 "2026-02-03" "750" state |> accepted

                    Expect.equal
                        (changes |> List.map _.Kind)
                        [ PartKind.Payment; PartKind.Payment; PartKind.Case ]
                        "Two entry effects and one final case effect"

                    Expect.equal
                        (changes
                         |> List.map (fun change -> EffectPosition.sequence change.Stamp.Position)
                         |> List.distinct)
                        [ Sequence.create 4L |> Option.get ]
                        "One accepted operation"

                    let after = apply state changes

                    Expect.equal
                        after.Case.Revision
                        (Revision.successor state.Case.Revision)
                        "One case revision"

                    Expect.isTrue
                        after.Payments[operation 3].Value.Removed
                        "Old assertion is removed"

                    Expect.equal
                        after.Payments[operation 3].Value.Entry
                        state.Payments[operation 3].Value.Entry
                        "Original value stays immutable"

                    Expect.isFalse
                        after.Payments[operation 4].Value.Removed
                        "Replacement is active"

                    let finance = after.Case.Value.Content |> Option.get |> _.Finance
                    Expect.equal (Amount.value finance.PaidAmount) 750M "Final money"
                    Expect.equal finance.PaymentCount 1L "Final active count"

                    Expect.equal
                        (finance.PaymentDates
                         |> Option.map (fun bounds ->
                             CalendarDate.format bounds.Earliest,
                             CalendarDate.format bounds.Latest))
                        (Some("2026-02-03", "2026-02-03"))
                        "Replacement extrema"

                    Expect.equal
                        (changes
                         |> List.choose PartChange.paymentDifference
                         |> List.map (function
                             | PaymentDifference.Removed _ -> "REMOVED"
                             | PaymentDifference.Recorded _ -> "RECORDED"))
                        [ "REMOVED"; "RECORDED" ]
                        "Ordered receipt directions")
            testCase "the remaining entries constrain replacement totals and extrema" (fun () ->
                let state = decided () |> pay 3 "2026-02-02" "250" |> pay 4 "2026-02-03" "100"
                let after = correct 5 3 "2026-02-04" "900" state |> accepted |> apply state
                let finance = after.Case.Value.Content |> Option.get |> _.Finance

                Expect.equal
                    (Amount.value finance.PaidAmount)
                    1000M
                    "Replacement is validated after removing the erroneous amount"

                Expect.equal finance.PaymentCount 2L "Unrelated payment stays active"

                Expect.equal
                    (finance.PaymentDates
                     |> Option.map (fun bounds ->
                         CalendarDate.format bounds.Earliest, CalendarDate.format bounds.Latest))
                    (Some("2026-02-03", "2026-02-04"))
                    "Both bounds update"

                correct 5 3 "2026-02-04" "901" state
                |> expectInputs
                |> List.exists (fun issue -> issue.Violation = Violation.PaymentExceedsPayable)
                |> fun found ->
                    Expect.isTrue found "Overpayment refuses before any accepted change")
            testCase "a removed or absent target cannot be corrected" (fun () ->
                let state = decided () |> pay 3 "2026-02-02" "10"

                correct 4 99 "2026-02-03" "10" state
                |> expectRefusal (Refusal.Case CaseRefusal.PaymentNotFound)

                let removed =
                    ClaimCore.Domain.Tasks.Case.RemovePayment.decide
                        (context 4)
                        environment
                        (Some state)
                        (Binding.CasePayment(target state, state.Case.Revision, operation 3))
                        InputMap.empty
                    |> accepted
                    |> apply state

                Expect.isError (correct 5 3 "2026-02-03" "10" removed) "Removed target refuses")
        ]
