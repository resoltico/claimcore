module ClaimCore.Tests.CorrectionMatrixTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private decisionWith amount =
    AuthoredCaseAct.Decide(
        ClaimCore.TestSupport.Inputs.decisionWith
            [ BusinessField.PayableAmount, (amount) ]
            (decision)
    )

let private states () =
    let zero = opened () |> apply (decisionWith "0") |> accepted

    [ opened (); decided (); zero; paid () ]
    |> List.collect (fun claim ->
        [ claim; claim |> apply (AuthoredCaseAct.Bare BareAct.Close) |> accepted ])

let private registrationChoices =
    [
        Amendment.Keep, false
        Amendment.Update(
            Inputs.registrationUpdate (
                ClaimCore.TestSupport.Inputs.registrationWith
                    [ BusinessField.ClaimantName, ("Synthetic matrix party") ]
                    (registration)
            )
        ),
        true
    ]

let private decisionChoices =
    [
        Change.Keep, "KEEP"
        Change.Update(
            Inputs.decisionUpdate (
                ClaimCore.TestSupport.Inputs.decisionWith
                    [
                        BusinessField.PayableAmount, ("650.0000")
                        BusinessField.PayableCurrency, ("USD")
                    ]
                    (decision)
            )
        ),
        "UPDATE"
        Change.Clear, "CLEAR"
    ]

let private paymentChoices =
    [
        PaymentChange.Keep, "KEEP"
        PaymentChange.Reaffirm, "REAFFIRM"
        PaymentChange.Replace(
            ClaimCore.TestSupport.Inputs.paymentFrom [ BusinessField.PaymentDate, ("2026-08-21") ]
        ),
        "REPLACE"
        PaymentChange.Clear, "CLEAR"
    ]

let private permitted (fields: CaseFields) registrationChanged decisionMode paymentMode =
    let decided = fields.Decision.IsSome
    let paid = fields.Payment.IsSome

    (fields.Status = CaseStatus.Closed || decided)
    && (decisionMode = "KEEP" || decided)
    && (paymentMode = "KEEP" || paid)
    && not (paid && decisionMode = "CLEAR" && paymentMode <> "CLEAR")
    && not (paid && decisionMode = "UPDATE" && paymentMode = "KEEP")
    && (registrationChanged
        || decisionMode <> "KEEP"
        || paymentMode = "REPLACE"
        || paymentMode = "CLEAR")

let private expectedFields (fields: CaseFields) registrationChanged decisionMode paymentMode =
    let registration =
        if registrationChanged then
            (ClaimCore.TestSupport.Inputs.registrationStateWith
                [
                    BusinessField.ClaimantName,
                    Admitted.Name(CaseFixtures.name "Synthetic matrix party")
                ]
                (fields.Registration))
        else
            fields.Registration

    let decision =
        match decisionMode with
        | "UPDATE" ->
            Some(
                ClaimCore.TestSupport.Inputs.decisionStateFrom
                    [
                        BusinessField.PaymentDecisionDate,
                        Admitted.Date(CaseFixtures.date "2026-08-15")
                        BusinessField.PayableAmount,
                        Admitted.Amount(Money.amount (CaseFixtures.money "650" "USD"))
                        BusinessField.PayableCurrency,
                        Admitted.Currency(Money.currencyValue (CaseFixtures.money "650" "USD"))
                    ]
            )
        | "CLEAR" -> None
        | _ -> fields.Decision

    let payment =
        match paymentMode with
        | "REPLACE" ->
            Some(
                ClaimCore.TestSupport.Inputs.paymentStateFrom
                    [ BusinessField.PaymentDate, Admitted.Date(CaseFixtures.date "2026-08-21") ]
            )
        | "CLEAR" -> None
        | _ -> fields.Payment

    { fields with
        Registration = registration
        Progress = ClaimCore.TestSupport.CaseFixtures.progress decision payment
    }

let private checkChoice
    before
    (registration, registrationChanged)
    (decision, decisionMode)
    (payment, paymentMode)
    =
    let snapshot = Case.snapshot before

    let act =
        AuthoredCaseAct.Correct
            {
                Registration = registration
                Decision = decision
                Payment = payment
            }

    let result = before |> apply act

    let expected =
        permitted (viewFields snapshot) registrationChanged decisionMode paymentMode

    Expect.equal (Result.isOk result) expected "Independent correction eligibility"

    match result with
    | Error _ -> Expect.isTrue (Case.snapshot before = snapshot) "Refusal preserves accepted state"
    | Ok changed ->
        let after = Case.snapshot changed

        Expect.equal
            after.Revision
            (CaseFixtures.revision (Revision.value snapshot.Revision + 1L))
            "One revision"

        Expect.isTrue
            ((viewFields after) =
                expectedFields (viewFields snapshot) registrationChanged decisionMode paymentMode)
            "Complete canonical result, reference and status"

let private matrix =
    testCase "all correction choices match an independent eight-state matrix" (fun () ->
        let mutable cases = 0

        for before in states () do
            for registration in registrationChoices do
                for decision in decisionChoices do
                    for payment in paymentChoices do
                        checkChoice before registration decision payment
                        cases <- cases + 1

        Expect.equal cases 192 "Every group choice across closure and payment axes")

let private zeroPaid =
    testCase "explicit paid reaffirmation cannot retain a zero replacement decision" (fun () ->
        let act =
            AuthoredCaseAct.Correct
                {
                    Registration = Amendment.Keep
                    Decision =
                        Change.Update(
                            Inputs.decisionUpdate (
                                ClaimCore.TestSupport.Inputs.decisionWith
                                    [ BusinessField.PayableAmount, ("0") ]
                                    (decision)
                            )
                        )
                    Payment =
                        PaymentChange.Replace(
                            ClaimCore.TestSupport.Inputs.paymentFrom
                                [ BusinessField.PaymentDate, ("2026-08-20") ]
                        )
                }

        Expect.equal
            (paid () |> apply act)
            (Error(Refusal.Case CaseRefusal.ZeroDecisionCannotBePaid))
            "No zero payment assertion"

        let cleared =
            match act with
            | AuthoredCaseAct.Correct value ->
                AuthoredCaseAct.Correct
                    { value with
                        Payment = PaymentChange.Clear
                    }
            | _ -> failtest "Synthetic correction shape"

        let after = paid () |> apply cleared |> accepted |> Case.snapshot

        Expect.isTrue
            (((viewFields after).Decision
              |> Option.map (fun value -> Money.format (Decision.payable (value))))
                =
                Some "0"
             && (viewFields after).Payment.IsNone)
            "An unpaid zero decision is valid")

[<Tests>]
let tests = testList "correction group invariants" [ matrix; zeroPaid ]
