module ClaimCore.Tests.Tasks.CaseCorrectTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Correct = ClaimCore.Domain.Tasks.Case.Correct

[<Tests>]
let tests =
    testList
        "task case.correct"
        [
            testCase "simultaneous chronology corrections use both proposed groups" (fun () ->
                let state = decided ()

                let values =
                    correction
                        "UPDATE"
                        "UPDATE"
                        [
                            text
                                (InputTarget.Field BusinessField.IncidentNotificationDate)
                                "2026-03-01"
                            text (InputTarget.Field BusinessField.PaymentDecisionDate) "2026-03-02"
                        ]

                let after =
                    Correct.decide (context 3) environment (Some state) (caseBinding state) values
                    |> accepted
                    |> apply state

                let content = after.Case.Value.Content |> Option.get

                Expect.equal
                    (CalendarDate.format (
                        Registration.incidentNotificationDate content.Registration
                    ))
                    "2026-03-01"
                    "Registration correction"

                Expect.equal
                    (content.Finance.Decision |> Option.map (Decision.date >> CalendarDate.format))
                    (Some "2026-03-02")
                    "Decision correction")
            testCase
                "no-op missing-group and paid-decision clearing refusals stay distinct"
                (fun () ->
                    let state = decided ()

                    Correct.decide
                        (context 3)
                        environment
                        (Some state)
                        (caseBinding state)
                        (correction "KEEP" "KEEP" [])
                    |> expectRefusal (Refusal.Case CaseRefusal.CorrectionNoChanges)

                    let closed = family () |> setCase Lifecycle.Active CaseStatus.Closed

                    Correct.decide
                        (context 3)
                        environment
                        (Some closed)
                        (caseBinding closed)
                        (correction "KEEP" "CLEAR" [])
                    |> expectRefusal (Refusal.Case CaseRefusal.CorrectionRequiresExistingValue)

                    let paid = state |> pay 3 "2026-02-02" "10"

                    Correct.decide
                        (context 4)
                        environment
                        (Some paid)
                        (caseBinding paid)
                        (correction "KEEP" "CLEAR" [])
                    |> expectRefusal (Refusal.Case CaseRefusal.DecisionHasPayments)

                    let offer = Correct.availability environment (Some paid) (caseBinding paid)

                    Expect.equal
                        offer.Choices[TaskInputId.Target(InputTarget.Group RecordGroup.Decision)]
                        [ "KEEP"; "UPDATE" ]
                        "Core constrains individual choices")
            testCase "only asserted groups require their respective capabilities" (fun () ->
                let state = decided ()

                let registrar =
                    AccessView.ofActor (
                        Some
                            {
                                Actor = actor
                                Grants =
                                    [
                                        {
                                            Role = Role.CaseRegistrar
                                            Scope = Scope.Installation
                                        }
                                    ]
                            }
                    )

                let environment =
                    { environment with
                        Requester = registrar
                    }

                Expect.isOk
                    (Correct.decide
                        (context 3)
                        environment
                        (Some state)
                        (caseBinding state)
                        (correction
                            "UPDATE"
                            "KEEP"
                            [ text (InputTarget.Field BusinessField.ClaimantName) "Corrected" ]))
                    "No decision assertion"

                Correct.decide
                    (context 3)
                    environment
                    (Some state)
                    (caseBinding state)
                    (correction "KEEP" "CLEAR" [])
                |> expectRefusal (Refusal.Access(AccessRefusal.NotAuthorized Capability.Decide)))
            testCase
                "closed paid corrections preserve payments and all independent current assertion issues"
                (fun () ->
                    let state =
                        decided ()
                        |> pay 3 "2026-02-02" "25"
                        |> setCase Lifecycle.Active CaseStatus.Closed

                    let context = context 5

                    let context =
                        { context with
                            Decision =
                                { context.Decision with
                                    BusinessDate = Some(System.DateOnly(2025, 12, 31))
                                }
                        }

                    let after =
                        Correct.decide
                            context
                            environment
                            (Some state)
                            (caseBinding state)
                            (correction
                                "KEEP"
                                "UPDATE"
                                [
                                    text
                                        (InputTarget.Field BusinessField.PayableAmount)
                                        "1200.0000"
                                    text (InputTarget.Field BusinessField.PayableCurrency) "EUR"
                                ])
                        |> accepted
                        |> apply state

                    Expect.equal
                        after.Case.Value.Status
                        CaseStatus.Closed
                        "Correction does not reopen"

                    Expect.equal after.Payments state.Payments "Every accepted entry is retained"

                    let invalid =
                        correction
                            "UPDATE"
                            "UPDATE"
                            [
                                text (InputTarget.Field BusinessField.IncidentDate) "2026-11-20"
                                text
                                    (InputTarget.Field BusinessField.IncidentNotificationDate)
                                    "2026-11-21"
                                text
                                    (InputTarget.Field BusinessField.ClaimantName)
                                    (String.replicate 201 "A")
                                text
                                    (InputTarget.Field BusinessField.InsurerName)
                                    (String.replicate 201 "B")
                                text
                                    (InputTarget.Field BusinessField.PaymentDecisionDate)
                                    "2026-11-20"
                            ]

                    let issues =
                        Correct.decide
                            (ClaimCore.Tests.TaskBuilders.context 5)
                            environment
                            (Some state)
                            (caseBinding state)
                            invalid
                        |> expectInputs

                    Expect.equal
                        (issues |> List.map _.Input)
                        [
                            TaskInputId.Target(InputTarget.Field BusinessField.IncidentDate)
                            TaskInputId.Target(
                                InputTarget.Field BusinessField.IncidentNotificationDate
                            )
                            TaskInputId.Target(InputTarget.Field BusinessField.ClaimantName)
                            TaskInputId.Target(InputTarget.Field BusinessField.InsurerName)
                            TaskInputId.Target(InputTarget.Field BusinessField.PaymentDecisionDate)
                            TaskInputId.Target(InputTarget.Field BusinessField.PaymentDecisionDate)
                            TaskInputId.Target(InputTarget.Field BusinessField.PaymentDecisionDate)
                        ]
                        "Current-date and both chronology relations remain distinct")
            testCase "equivalent scalar spelling remains a correction no-op" (fun () ->
                let state = decided ()

                Correct.decide
                    (context 3)
                    environment
                    (Some state)
                    (caseBinding state)
                    (correction
                        "KEEP"
                        "UPDATE"
                        [
                            text (InputTarget.Field BusinessField.PayableAmount) "1000.0000"
                            text (InputTarget.Field BusinessField.PayableCurrency) "EUR"
                        ])
                |> expectRefusal (Refusal.Case CaseRefusal.CorrectionNoChanges))
            testCase "description-only paid corrections preserve all financial facts" (fun () ->
                let state = decided () |> pay 3 "2026-02-02" "25.1234"

                let values =
                    correction
                        "UPDATE"
                        "KEEP"
                        [
                            text
                                (InputTarget.Field BusinessField.IncidentDescription)
                                "A corrected paragraph\nwith joining marks العربية"
                        ]

                let after =
                    Correct.decide (context 4) environment (Some state) (caseBinding state) values
                    |> accepted
                    |> apply state

                Expect.equal
                    after.Payments
                    state.Payments
                    "No payment acknowledgement or re-recording"

                Expect.equal
                    (after.Case.Value.Content |> Option.map _.Finance)
                    (state.Case.Value.Content |> Option.map _.Finance)
                    "Exact decision and totals retained"

                Expect.equal
                    (after.Case.Value.Content
                     |> Option.bind (fun content -> Registration.description content.Registration)
                     |> Option.map IncidentDescription.format)
                    (Some "A corrected paragraph\nwith joining marks العربية")
                    "Authored text is not normalized")
        ]
