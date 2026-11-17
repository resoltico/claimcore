module ClaimCore.Tests.DecisionFactsTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Tests.TaskBuilders

let private date value = CalendarDate.parse value |> accepted

let private scope =
    {
        FamilyFactScope.Payments = Set.ofList [ operation 3; operation 6 ]
        Reminders = Set.singleton (ReminderId.ofOperationId (operation 6))
        Notes = Set.singleton (NoteId.ofOperationId (operation 6))
        Operation = Some(operation 6)
    }

let private acquired () =
    let before = decided () |> pay 3 "2026-02-02" "25" |> pay 4 "2026-02-03" "10"

    let reminders =
        ClaimCore.Domain.Tasks.Reminder.Create.decide
            (context 5)
            environment
            (Some(DecisionFacts.fromReplay before))
            (Binding.ReminderNew(target before))
            (planInputs "Unrelated pending work" "2026-11-16")
        |> accepted

    let complete = apply before reminders

    let case =
        {
            PartChange.Aggregate = AggregateReference.Family complete.Id
            PartId = CaseId.value complete.Id
            Revision = complete.Case.Revision
            NaturalKey = None
            Value =
                PartValue.Case
                    {
                        Reference = None
                        Lifecycle = complete.Case.Value.Lifecycle
                        Status = complete.Case.Value.Status
                        Content = complete.Case.Value.Content
                        Disposition = None
                    }
            Erases = None
            Stamp = complete.Case.Stamp
        }

    let payment = complete.Payments[operation 3]

    let selected =
        { case with
            PartId = OperationId.value (operation 3)
            Revision = payment.Revision
            Value = PartValue.Payment payment.Value
            Stamp = payment.Stamp
        }

    let origins =
        Map.ofList
            [
                (PartKind.Case, CaseId.value complete.Id), initialChange.Stamp
                (PartKind.Payment, OperationId.value (operation 3)), payment.Stamp
            ]

    let finance = complete.Case.Value.Content |> Option.get |> _.Finance

    let summary =
        {
            LiveFamilySummary.PaymentCount = finance.PaymentCount
            Paid = finance.PaidAmount
            PaymentDates = finance.PaymentDates
            RemainingPaymentDates =
                Some(operation 3, PaymentDateBounds.create (date "2026-02-03") (date "2026-02-03"))
            NextPendingDue = Some(date "2026-11-16")
            RemainingPendingDue = Some(date "2026-11-16")
        }

    let selected = [ case; selected ]

    let facts =
        DecisionFacts.restore
            complete.Id
            complete.Reference
            complete.Sequence
            selected
            origins
            summary
            scope
        |> accepted

    complete, selected, origins, summary, facts

[<Tests>]
let tests =
    testList
        "live decision facts"
        [
            testCase
                "selected facts preserve unrelated money and due projections without becoming a complete family"
                (fun () ->
                    let complete, parts, origins, _, facts = acquired ()
                    Expect.equal facts.Payments.Count 1 "Only the selected payment is materialized"

                    Expect.isEmpty
                        facts.Reminders
                        "The unrelated reminder is represented by an explicit due fact"

                    Expect.equal
                        (facts.Case.Value.Content |> Option.get |> _.Finance.PaymentCount)
                        2L
                        "The root owns the complete financial count"

                    Expect.equal
                        (CaseFamily.restore
                            complete.Id
                            complete.Reference
                            complete.Sequence
                            parts
                            origins)
                        (Error RecordViolation.InvalidPaymentState)
                        "The same partial part set is rejected by complete reconstruction"

                    let changes =
                        ClaimCore.Domain.Tasks.Case.RemovePayment.decide
                            (context 6)
                            environment
                            (Some facts)
                            (Binding.CasePayment(
                                target complete,
                                complete.Case.Revision,
                                operation 3
                            ))
                            InputMap.empty
                        |> accepted

                    let live = DecisionFacts.apply (Some facts) changes |> accepted
                    let replay = apply complete changes

                    Expect.equal
                        (DecisionFacts.snapshot live)
                        (CaseFamily.snapshot replay)
                        "Complete apply independently reconciles all payments"

                    let _, index = DecisionFacts.rows live changes |> accepted

                    Expect.equal
                        index
                        (CaseFamily.index replay |> Option.get)
                        "Unrelated due, responsibility, search and money are preserved"

                    Expect.equal
                        index.FollowUp
                        (Some(date "2026-11-16"))
                        "No reminder child map was needed for the exact next due"

                    Expect.equal
                        (live.Case.Value.Content
                         |> Option.get
                         |> _.Finance.PaidAmount
                         |> Amount.value)
                        10M
                        "Only the selected amount is removed")
            testCase
                "a plausible financial root that disagrees with accepted payment deltas refuses"
                (fun () ->
                    let complete, _, _, _, facts = acquired ()

                    let changes =
                        ClaimCore.Domain.Tasks.Case.RemovePayment.decide
                            (context 6)
                            environment
                            (Some facts)
                            (Binding.CasePayment(
                                target complete,
                                complete.Case.Revision,
                                operation 3
                            ))
                            InputMap.empty
                        |> accepted

                    let forged =
                        changes
                        |> List.map (fun change ->
                            match change.Value with
                            | PartValue.Case value ->
                                let content = value.Content |> Option.get

                                let finance =
                                    FinancialPosition.create
                                        content.Finance.Decision
                                        (Amount.parse "11" |> accepted)
                                        content.Finance.PaymentCount
                                        content.Finance.PaymentDates
                                    |> accepted

                                { change with
                                    Value =
                                        PartValue.Case
                                            { value with
                                                Content = Some { content with Finance = finance }
                                            }
                                }
                            | _ -> change)

                    Expect.equal
                        (DecisionFacts.apply (Some facts) forged)
                        (Error RecordViolation.InvalidPaymentState)
                        "The check detects an inconsistent paid delta rather than accepting a plausible result")
            testCase
                "selected private values and remainder correlation cannot silently become omissions"
                (fun () ->
                    let complete, parts, origins, summary, _ = acquired ()

                    let missing =
                        parts
                        |> List.map (fun change ->
                            match change.Value with
                            | PartValue.Payment value ->
                                { change with
                                    Value = PartValue.Payment { value with Entry = None }
                                }
                            | _ -> change)

                    Expect.isError
                        (DecisionFacts.restore
                            complete.Id
                            complete.Reference
                            complete.Sequence
                            missing
                            origins
                            summary
                            scope)
                        "A consumed selected payment needs its admitted value"

                    let wrong =
                        { summary with
                            RemainingPaymentDates =
                                summary.RemainingPaymentDates
                                |> Option.map (fun (_, dates) -> operation 99, dates)
                        }

                    Expect.isError
                        (DecisionFacts.restore
                            complete.Id
                            complete.Reference
                            complete.Sequence
                            parts
                            origins
                            wrong
                            scope)
                        "A remainder for another selection cannot construct admitted decision facts")
            testCase
                "unqueried selections and another operation cannot masquerade as known absence"
                (fun () ->
                    let complete, _, _, _, facts = acquired ()

                    Expect.throwsT<System.ArgumentException>
                        (fun () ->
                            ClaimCore.Domain.Tasks.Note.Remove.decide
                                (context 6)
                                environment
                                (Some facts)
                                (Binding.Note(
                                    target complete,
                                    NoteId.ofOperationId (operation 99)
                                ))
                                InputMap.empty
                            |> ignore)
                        "A child outside the acquired scope is a programming error, not a plausible missing resource"

                    Expect.equal
                        (ClaimCore.Domain.Tasks.Note.Remove.decide
                            (context 6)
                            environment
                            (Some facts)
                            (Binding.Note(target complete, NoteId.ofOperationId (operation 6)))
                            InputMap.empty)
                        (Error(Rejection.Refused(Refusal.Access AccessRefusal.ResourceUnavailable)))
                        "An explicitly queried absent child retains its actual refusal meaning"

                    Expect.throwsT<System.ArgumentException>
                        (fun () ->
                            ClaimCore.Domain.Tasks.Case.RemovePayment.decide
                                (context 7)
                                environment
                                (Some facts)
                                (Binding.CasePayment(
                                    target complete,
                                    complete.Case.Revision,
                                    operation 3
                                ))
                                InputMap.empty
                            |> ignore)
                        "A different authoring identity requires fresh acquisition")
        ]
