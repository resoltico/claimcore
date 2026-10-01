module ClaimCore.Tests.CorrectionMatrixTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private decisionWith amount =
    Command.Decide { decision with PayableAmount = amount }

let private states () =
    let zero = opened () |> apply (decisionWith "0") |> accepted

    [ opened (); decided (); zero; paid () ]
    |> List.collect (fun claim -> [ claim; claim |> apply Command.Close |> accepted ])

let private registrationChoices =
    [
        RegistrationCorrection.Keep, false
        RegistrationCorrection.Replace
            { registration with
                ClaimantName = "Synthetic matrix party"
            },
        true
    ]

let private decisionChoices =
    [
        DecisionCorrection.Keep, "KEEP"
        DecisionCorrection.Replace
            { decision with
                PayableAmount = "650.0000"
                PayableCurrency = "USD"
            },
        "REPLACE"
        DecisionCorrection.Clear, "CLEAR"
    ]

let private paymentChoices =
    [
        PaymentCorrection.Keep, "KEEP"
        PaymentCorrection.Replace "2026-08-21", "REPLACE"
        PaymentCorrection.Clear, "CLEAR"
    ]

let private permitted (fields: CaseFields) registrationChanged decisionMode paymentMode =
    let decided = fields.PaymentDecisionDate.IsSome
    let paid = fields.PaymentDate.IsSome

    (fields.Status = CaseStatus.Closed || decided)
    && (decisionMode = "KEEP" || decided)
    && (paymentMode = "KEEP" || paid)
    && not (paid && decisionMode = "CLEAR" && paymentMode <> "CLEAR")
    && not (paid && decisionMode = "REPLACE" && paymentMode = "KEEP")
    && (registrationChanged || decisionMode <> "KEEP" || paymentMode <> "KEEP")

let private expectedFields (fields: CaseFields) registrationChanged decisionMode paymentMode =
    let registration =
        if registrationChanged then
            { fields with
                ClaimantName = "Synthetic matrix party"
            }
        else
            fields

    let decision =
        match decisionMode with
        | "REPLACE" ->
            { registration with
                PaymentDecisionDate = Some "2026-08-15"
                PayableAmount = Some "650"
                PayableCurrency = Some "USD"
            }
        | "CLEAR" ->
            { registration with
                PaymentDecisionDate = None
                PayableAmount = None
                PayableCurrency = None
            }
        | _ -> registration

    match paymentMode with
    | "REPLACE" ->
        { decision with
            PaymentDate = Some "2026-08-21"
        }
    | "CLEAR" -> { decision with PaymentDate = None }
    | _ -> decision

let private checkChoice
    before
    (registration, registrationChanged)
    (decision, decisionMode)
    (payment, paymentMode)
    =
    let snapshot = Claim.view before

    let command =
        Command.CorrectCase
            {
                Registration = registration
                Decision = decision
                Payment = payment
            }

    let result = before |> apply command

    let expected =
        permitted snapshot.Fields registrationChanged decisionMode paymentMode

    Expect.equal (Result.isOk result) expected "Independent correction eligibility"

    match result with
    | Error _ -> Expect.isTrue (Claim.view before = snapshot) "Refusal preserves accepted state"
    | Ok changed ->
        let after = Claim.view changed
        Expect.equal after.Version (snapshot.Version + 1L) "One revision"

        Expect.isTrue
            (after.Fields =
                expectedFields snapshot.Fields registrationChanged decisionMode paymentMode)
            "Complete canonical result, reference and status"

let private matrix =
    testCase
        "[CC-DOM-002] all correction choices match an independent eight-state matrix"
        (fun () ->
            let mutable cases = 0

            for before in states () do
                for registration in registrationChoices do
                    for decision in decisionChoices do
                        for payment in paymentChoices do
                            checkChoice before registration decision payment
                            cases <- cases + 1

            Expect.equal cases 144 "Every group choice across closure and payment axes")

let private zeroPaid =
    testCase
        "[CC-DOM-001] explicit paid reaffirmation cannot retain a zero replacement decision"
        (fun () ->
            let command =
                Command.CorrectCase
                    {
                        Registration = RegistrationCorrection.Keep
                        Decision = DecisionCorrection.Replace { decision with PayableAmount = "0" }
                        Payment = PaymentCorrection.Replace "2026-08-20"
                    }

            Expect.equal
                (paid () |> apply command)
                (Error DomainError.ZeroDecisionCannotBePaid)
                "No zero payment assertion"

            let cleared =
                match command with
                | Command.CorrectCase value ->
                    Command.CorrectCase
                        { value with
                            Payment = PaymentCorrection.Clear
                        }
                | _ -> failtest "Synthetic correction shape"

            let after = paid () |> apply cleared |> accepted |> Claim.view

            Expect.isTrue
                (after.Fields.PayableAmount = Some "0" && after.Fields.PaymentDate.IsNone)
                "An unpaid zero decision is valid")

let tests = testList "correction group invariants" [ matrix; zeroPaid ]
