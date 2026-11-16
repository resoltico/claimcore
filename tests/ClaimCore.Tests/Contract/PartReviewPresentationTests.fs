module ClaimCore.Tests.PartReviewPresentationTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Domain.Changes
open ClaimCore.Presentation
open ClaimCore.TestSupport

let private id = Guid.Parse "10000000-0000-7000-8000-000000000001"

let private references =
    {
        ChangeReferences.Actors = []
        Cases = []
    }

let private fields =
    ResponseExamples.snapshot.State |> CaseState.fields |> Option.get

let private caseValue status finance =
    {
        CaseValue.Reference = None
        Lifecycle = Lifecycle.Active
        Status = status
        Content =
            Some
                {
                    Registration = fields.Registration
                    Finance = finance
                }
        Disposition = None
    }

[<Tests>]
let tests =
    testList
        "shared part review facts"
        [
            testCase
                "unchanged case money retains its unit and is not split into scalar currency fields"
                (fun () ->
                    let before = caseValue CaseStatus.Opened fields.Finance
                    let after = caseValue CaseStatus.Closed fields.Finance

                    let description =
                        CaseDescription.describe fields.CaseReference (Some before) after

                    let _, unchanged =
                        ReviewScreen.describePart references id (PartDescription.Case description)

                    let claimed = unchanged |> List.find (fun entry -> entry.Key = "claimedAmount")

                    match claimed.Value with
                    | PresentedValue.Money money ->
                        Expect.equal (Money.format money.Value) "1000" "Unchanged amount"
                        Expect.equal (Money.currency money.Value) "EUR" "Unchanged monetary unit"
                    | other -> failtestf "Expected atomic money, got %A" other

                    Expect.isFalse
                        (unchanged |> List.exists (fun entry -> entry.Key = "claimedCurrency"))
                        "Currency is part of the money value")
            testCase
                "a currency-only correction changes money rather than labeling its currency unchanged"
                (fun () ->
                    let decision = DomainInputs.admittedDecision DomainInputs.decision

                    let finance =
                        FinancialPosition.withDecision (Some decision) fields.Finance
                        |> Result.toOption
                        |> Option.get

                    let changedDecision =
                        Decision.replace
                            [
                                BusinessField.PayableCurrency,
                                Admitted.Currency(
                                    Currency.parse "USD" |> Result.toOption |> Option.get
                                )
                            ]
                            decision
                        |> Result.toOption
                        |> Option.get

                    let changedFinance =
                        FinancialPosition.withDecision (Some changedDecision) finance
                        |> Result.toOption
                        |> Option.get

                    let description =
                        CaseDescription.describe
                            fields.CaseReference
                            (Some(caseValue CaseStatus.Opened finance))
                            (caseValue CaseStatus.Opened changedFinance)

                    let changes, unchanged =
                        ReviewScreen.describePart references id (PartDescription.Case description)

                    let money =
                        changes
                        |> List.find (fun change ->
                            change.Key.EndsWith(".payableAmount", StringComparison.Ordinal))

                    match money.Before, money.After with
                    | PresentedValue.Money before, PresentedValue.Money after ->
                        Expect.equal
                            (Money.currency before.Value, Money.currency after.Value)
                            ("EUR", "USD")
                            "Both authoritative units are visible"
                    | other -> failtestf "Expected a monetary difference: %A" other

                    Expect.isFalse
                        (unchanged
                         |> List.exists (fun entry ->
                             List.contains entry.Key [ "payableAmount"; "payableCurrency" ]))
                        "Changed money is not declared unchanged")
            testCase
                "person descriptions retain kind, issuer and subject as separate labeled facts"
                (fun () ->
                    let principal =
                        PrincipalKey.human "https://issuer.example/" "subject-1"
                        |> Result.toOption
                        |> Option.get

                    let value =
                        {
                            PersonValue.Principal = Some principal
                            Enabled = true
                            Profile = ProfileValue.Absent
                            Grants = []
                        }

                    let description = PersonDescription.describe None value

                    let changes, _ =
                        ReviewScreen.describePart
                            references
                            id
                            (PartDescription.Person description)

                    let value label =
                        changes |> List.find (fun change -> change.LabelId = label) |> _.After

                    Expect.equal
                        (value "field.principal.kind.label")
                        (PresentedValue.Code("principalKind", "HUMAN"))
                        "Account kind"

                    Expect.equal
                        (value "field.principal.issuer.label")
                        (PresentedValue.Identifier "https://issuer.example/")
                        "Issuer"

                    Expect.equal
                        (value "field.principal.subject.label")
                        (PresentedValue.Identifier "subject-1")
                        "Subject")
            testCase
                "unchanged profiles and installation scopes remain visible and localizable"
                (fun () ->
                    let person = ClaimCore.Tests.TaskBuilders.person ()

                    let value =
                        {
                            PersonValue.Principal = None
                            Enabled = false
                            Profile = person.Profile
                            Grants =
                                [
                                    {
                                        Grant =
                                            {
                                                Role = Role.CaseReader
                                                Scope = Scope.Installation
                                            }
                                        Active = true
                                    }
                                ]
                        }

                    let description = PersonDescription.describe (Some person) value

                    let changes, unchanged =
                        ReviewScreen.describePart
                            references
                            id
                            (PartDescription.Person description)

                    Expect.contains
                        (unchanged |> List.map _.LabelId)
                        "field.profile.displayName.label"
                        "Unchanged profile name"

                    let role =
                        changes
                        |> List.find (fun change -> change.LabelId = "role.CASE_READER.label")

                    Expect.equal
                        role.After
                        (PresentedValue.Code("scope", "INSTALLATION"))
                        "Scope uses the translated vocabulary, not raw identifier text")
        ]
