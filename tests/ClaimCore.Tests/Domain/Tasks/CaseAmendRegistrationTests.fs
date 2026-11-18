module ClaimCore.Tests.Tasks.CaseAmendRegistrationTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Amend = ClaimCore.Domain.Tasks.Case.AmendRegistration

[<Tests>]
let tests =
    testList
        "task case.amendRegistration"
        [
            testCase
                "retained nonconforming text is reaffirmed, while changed text uses current policy"
                (fun () ->
                    for oldName in [ "Synthetic\u202eParty"; String.replicate 201 "A" ] do
                        let initial =
                            match initialChange.Value with
                            | PartValue.Case value -> value
                            | _ -> failtest "Expected case fixture."

                        let content = initial.Content.Value

                        let registration =
                            Registration.replace
                                [
                                    BusinessField.PolicyNumber,
                                    Admitted.Identifier(Identifier.parse oldName |> accepted)
                                ]
                                content.Registration
                            |> accepted

                        let part =
                            { initialChange with
                                Value =
                                    PartValue.Case
                                        { initial with
                                            Content =
                                                Some
                                                    { content with
                                                        Registration = registration
                                                    }
                                        }
                            }

                        let state = CaseFamily.apply None part |> accepted

                        let unchanged =
                            inputs [ text (InputTarget.Field BusinessField.PolicyNumber) oldName ]

                        Amend.decide
                            (context 2)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            unchanged
                        |> accepted
                        |> ignore

                        let changed =
                            inputs
                                [
                                    text
                                        (InputTarget.Field BusinessField.PolicyNumber)
                                        (oldName + "x")
                                ]

                        Expect.isEmpty
                            (Amend.check changed)
                            "Permanent syntax does not preempt state-aware admission"

                        let issues =
                            Amend.decide
                                (context 2)
                                environment
                                (Some(DecisionFacts.fromReplay state))
                                (caseBinding state)
                                changed
                            |> expectInputs

                        Expect.isFalse issues.IsEmpty "Changed assertions follow current policy")
            testCase
                "partial updates preserve omission and permit clearing optional facts"
                (fun () ->
                    let state = family ()

                    let values =
                        inputs
                            [
                                text (InputTarget.Field BusinessField.PolicyNumber) "P-1"
                                text
                                    (InputTarget.Field BusinessField.PolicyNumber)
                                    "Changed claimant"
                            ]

                    let state =
                        Amend.decide
                            (context 2)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            values
                        |> accepted
                        |> apply state

                    let after =
                        Amend.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            (inputs
                                [
                                    TaskInputId.Target(
                                        InputTarget.Field BusinessField.PolicyNumber
                                    ),
                                    InputValue.Null
                                ])
                        |> accepted
                        |> apply state

                    let registration = after.Case.Value.Content |> Option.get |> _.Registration

                    Expect.equal
                        (Identifier.format (Registration.policyNumber registration |> Option.get))
                        "Changed claimant"
                        "Omitted value is kept"

                    Expect.isNone (Registration.policyNumber registration) "Explicit clearing"

                    Expect.equal
                        (Money.value (Registration.claimed registration |> Option.get))
                        1000M
                        "Money was not re-authored")
            testCase "closed and decided cases do not offer ordinary amendment" (fun () ->
                for state, headline in
                    [
                        family () |> setCase Lifecycle.Active CaseStatus.Closed,
                        CaseHeadline.Closed
                        decided (), CaseHeadline.Decided
                    ] do
                    Amend.decide
                        (context 4)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (caseBinding state)
                        (inputs [ text (InputTarget.Field BusinessField.PolicyNumber) "Changed" ])
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered headline)))
            testCase
                "reviewed amendment clears received without changing the earlier revision"
                (fun () ->
                    let state = family ()
                    let field = TaskInputId.Target(InputTarget.Field BusinessField.ReceivedDate)

                    let clear =
                        InputMap.create (Map.ofList [ field, InputValue.Null ]) Map.empty
                        |> Option.get

                    Amend.decide
                        (context 2)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (Binding.Case(target state, Revision.zero))
                        clear
                    |> expectRefusal (Refusal.revisionConflict Revision.first)

                    let after =
                        Amend.decide
                            (context 2)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            clear
                        |> accepted
                        |> apply state

                    let received (family: CaseFamily) =
                        family.Case.Value.Content
                        |> Option.bind (fun content ->
                            Registration.receivedDate content.Registration)

                    Expect.isSome (received state) "The earlier revision retains the date"
                    Expect.isNone (received after) "The current fact is absent")
            testCase
                "retained dates survive clock rollback while new date assertions use the action date"
                (fun () ->
                    let state = family ()
                    let context = context 2

                    let context =
                        { context with
                            Decision =
                                { context.Decision with
                                    BusinessDate = Some(System.DateOnly(2025, 12, 31))
                                }
                        }

                    let after =
                        Amend.decide
                            context
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            (inputs
                                [
                                    text
                                        (InputTarget.Field BusinessField.PolicyNumber)
                                        "Changed after rollback"
                                    text
                                        (InputTarget.Field BusinessField.IncidentDate)
                                        "2026-01-01"
                                ])
                        |> accepted
                        |> apply state

                    Expect.equal
                        (after.Case.Value.Content
                         |> Option.map (fun content ->
                             CalendarDate.format (
                                 Registration.incidentDate content.Registration |> Option.get
                             )))
                        (Some "2026-01-01")
                        "An unchanged accepted value is not re-admitted"

                    let issues =
                        Amend.decide
                            context
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            (inputs
                                [
                                    text
                                        (InputTarget.Field BusinessField.ReceivedDate)
                                        "2026-01-03"
                                ])
                        |> expectInputs

                    Expect.isTrue
                        (issues
                         |> List.exists (fun issue -> issue.Violation = Violation.FutureDate))
                        "A different date is a current assertion")
            testCase
                "an empty update is malformed even when the reviewed version is stale"
                (fun () ->
                    let state = family ()

                    let issues =
                        Amend.decide
                            (context 2)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (Binding.Case(target state, Revision.zero))
                            InputMap.empty
                        |> expectInputs

                    Expect.equal
                        issues.Head.Violation
                        Violation.UpdateRequired
                        "The intended admission rule is exercised")
            testCase
                "optional narrative and identifiers share ordinary keep and clear semantics"
                (fun () ->
                    let state = family ()

                    let values =
                        inputs
                            [
                                text (InputTarget.Field BusinessField.PolicyNumber) "POL-1"
                                text
                                    (InputTarget.Field BusinessField.InsurerClaimReference)
                                    "INS-1"
                                text
                                    (InputTarget.Field BusinessField.IncidentDescription)
                                    "Paragraph one\nParagraph two"
                            ]

                    let state =
                        Amend.decide
                            (context 2)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            values
                        |> accepted
                        |> apply state

                    let values =
                        inputs
                            [
                                TaskInputId.Target(
                                    InputTarget.Field BusinessField.IncidentDescription
                                ),
                                InputValue.Null
                                TaskInputId.Target(
                                    InputTarget.Field BusinessField.InsurerClaimReference
                                ),
                                InputValue.Null
                            ]

                    let after =
                        Amend.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            values
                        |> accepted
                        |> apply state

                    let registration = after.Case.Value.Content |> Option.get |> _.Registration

                    Expect.equal
                        (Registration.policyNumber registration |> Option.map Identifier.format)
                        (Some "POL-1")
                        "Omission preserves the policy identifier"

                    Expect.isNone
                        (Registration.insurerClaimReference registration)
                        "Identifier clearing"

                    Expect.isNone (Registration.description registration) "Narrative clearing"

                    Expect.equal
                        (Registration.claimed registration |> Option.get |> Money.value)
                        1000M
                        "Unrelated money stays exact")
        ]
