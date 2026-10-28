module ClaimCore.Tests.CorrectionMatrixTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private decisionWith amount =
    AuthoredCaseAct.Decide { decision with PayableAmount = amount }

let private states () =
    let zero = opened () |> apply (decisionWith "0") |> accepted

    [ opened (); decided (); zero; paid () ]
    |> List.collect (fun claim -> [ claim; claim |> apply AuthoredCaseAct.Close |> accepted ])

let private registrationChoices =
    [
        Amendment.Keep, false
        Amendment.Replace
            { registration with
                ClaimantName = "Synthetic matrix party"
            },
        true
    ]

let private decisionChoices =
    [
        Change.Keep, "KEEP"
        Change.Replace
            { decision with
                PayableAmount = "650.0000"
                PayableCurrency = "USD"
            },
        "REPLACE"
        Change.Clear, "CLEAR"
    ]

let private paymentChoices =
    [
        Change.Keep, "KEEP"
        Change.Replace
            {
                PaymentInput.PaymentDate = "2026-08-21"
            },
        "REPLACE"
        Change.Clear, "CLEAR"
    ]

let private permitted (fields: CaseFields) registrationChanged decisionMode paymentMode =
    let decided = fields.Decision.IsSome
    let paid = fields.Payment.IsSome

    (fields.Status = CaseStatus.Closed || decided)
    && (decisionMode = "KEEP" || decided)
    && (paymentMode = "KEEP" || paid)
    && not (paid && decisionMode = "CLEAR" && paymentMode <> "CLEAR")
    && not (paid && decisionMode = "REPLACE" && paymentMode = "KEEP")
    && (registrationChanged || decisionMode <> "KEEP" || paymentMode <> "KEEP")

let private expectedFields (fields: CaseFields) registrationChanged decisionMode paymentMode =
    let registration =
        if registrationChanged then
            { fields.Registration with
                ClaimantName = CaseFixtures.name "Synthetic matrix party"
            }
        else
            fields.Registration

    let decision =
        match decisionMode with
        | "REPLACE" ->
            Some
                {
                    PaymentDecisionDate = CaseFixtures.date "2026-08-15"
                    PayableAmount = CaseFixtures.money "650" "USD"
                }
        | "CLEAR" -> None
        | _ -> fields.Decision

    let payment =
        match paymentMode with
        | "REPLACE" ->
            Some
                {
                    Payment.PaymentDate = CaseFixtures.date "2026-08-21"
                }
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

        Expect.equal cases 144 "Every group choice across closure and payment axes")

let private zeroPaid =
    testCase "explicit paid reaffirmation cannot retain a zero replacement decision" (fun () ->
        let act =
            AuthoredCaseAct.Correct
                {
                    Registration = Amendment.Keep
                    Decision = Change.Replace { decision with PayableAmount = "0" }
                    Payment = Change.Replace { PaymentDate = "2026-08-20" }
                }

        Expect.equal
            (paid () |> apply act)
            (Error(Refusal.Case CaseRefusal.ZeroDecisionCannotBePaid))
            "No zero payment assertion"

        let cleared =
            match act with
            | AuthoredCaseAct.Correct value ->
                AuthoredCaseAct.Correct { value with Payment = Change.Clear }
            | _ -> failtest "Synthetic correction shape"

        let after = paid () |> apply cleared |> accepted |> Case.snapshot

        Expect.isTrue
            (((viewFields after).Decision
              |> Option.map (fun value -> Money.format value.PayableAmount))
                =
                Some "0"
             && (viewFields after).Payment.IsNone)
            "An unpaid zero decision is valid")

[<Tests>]
let tests = testList "correction group invariants" [ matrix; zeroPaid ]
