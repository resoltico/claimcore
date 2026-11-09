module ClaimCore.Tests.FinancialCaseDecisionTests

open System
open Expecto
open ClaimCore.Domain

let private accepted value =
    Result.defaultWith (fun error -> failtestf "%A" error) value

let private today = DateOnly(2026, 1, 31)

let private id number =
    Guid.Parse(sprintf "019aa6a0-0000-7000-8000-%012d" number)

let private registration =
    RegistrationInput.create
        [
            BusinessField.IncidentDate, "2026-01-01"
            BusinessField.IncidentNotificationDate, "2026-01-02"
            BusinessField.IncidentCountry, "LV"
            BusinessField.ClaimantName, "Claimant"
            BusinessField.InsurerName, "Insurer"
            BusinessField.ClaimedAmount, "100"
            BusinessField.ClaimedCurrency, "EUR"
        ]
    |> accepted

let private decision amount =
    DecisionInput.create
        [
            BusinessField.PaymentDecisionDate, "2026-01-10"
            BusinessField.PayableAmount, amount
            BusinessField.PayableCurrency, "EUR"
        ]
    |> accepted

let private payment amount day =
    PaymentInput.create
        [
            BusinessField.PaymentDate, day
            BusinessField.PaidAmount, amount
            BusinessField.PaidCurrency, "EUR"
        ]
    |> accepted

let private command number (current: CaseSnapshot option) act =
    Case.admit
        today
        current
        {
            OperationId = id number
            CaseReference = "FIN-1"
            ExpectedRevision =
                current
                |> Option.map (fun value -> Revision.value value.Revision)
                |> Option.defaultValue 0L
            Act = act
        }
    |> accepted

let private run number context current act =
    Case.decideSnapshot today context (command number current act) current

let private initial () =
    let opened =
        run 1 CasePaymentContext.NoneRequired None (CaseAct.Open registration)
        |> accepted

    run 2 CasePaymentContext.NoneRequired (Some opened.After) (CaseAct.Decide(decision "100"))
    |> accepted
    |> _.After

let private fields snapshot =
    CaseState.fields snapshot.State |> Option.get

[<Tests>]
let tests =
    testList
        "bounded case financial decisions"
        [
            testCase
                "the actual case decision accepts fifty-one distinct payments with bounded effects"
                (fun () ->
                    let start = initial ()
                    let mutable current = start
                    let identities = Collections.Generic.HashSet<OperationId>()

                    for number in 3..53 do
                        let context =
                            CasePaymentContext.Recording(current.CaseId, current.Revision, false)

                        let result =
                            run
                                number
                                context
                                (Some current)
                                (CaseAct.RecordPayment(payment "1" "2026-01-11"))
                            |> accepted

                        match result.Payment with
                        | Some(PaymentDifference.Recorded entry) ->
                            Expect.isTrue
                                (identities.Add(PaymentEntry.id entry))
                                "Every accepted payment identity is distinct"

                            Expect.equal
                                (Amount.value (PaymentEntry.amount entry))
                                1M
                                "One exact changed entry"
                        | _ -> failtest "Recording emits its bounded entry effect"

                        current <- result.After

                    let finance = (fields current).Finance

                    Expect.equal
                        finance.PaymentCount
                        51L
                        "Actual case admission has no total-50 cap"

                    Expect.equal (Amount.value finance.PaidAmount) 51M "Exact aggregate paid"

                    Expect.equal
                        (Money.value (FinancialPosition.totals finance |> Option.get).Remaining)
                        49M
                        "Derived remaining"

                    Expect.equal
                        (Revision.value current.Revision)
                        53L
                        "Independent case revision advances once per command")
            testCase
                "removal uses selected active entry and remaining bounds without full history"
                (fun () ->
                    let start = initial ()

                    let paid =
                        run
                            3
                            (CasePaymentContext.Recording(start.CaseId, start.Revision, false))
                            (Some start)
                            (CaseAct.RecordPayment(payment "5" "2026-01-11"))
                        |> accepted

                    let selected =
                        match paid.Payment with
                        | Some(PaymentDifference.Recorded value) -> value
                        | _ -> failtest "Payment effect required"

                    let result =
                        run
                            4
                            (CasePaymentContext.Removing(
                                paid.After.CaseId,
                                paid.After.Revision,
                                Some selected,
                                None
                            ))
                            (Some paid.After)
                            (CaseAct.RemovePayment(OperationId.value (PaymentEntry.id selected)))
                        |> accepted

                    Expect.equal (fields result.After).Finance.PaymentCount 0L "Sole entry removed"

                    Expect.equal
                        result.Payment
                        (Some(PaymentDifference.Removed selected))
                        "Exact selected effect retained"

                    Expect.equal
                        (CaseProgress.ofFinancial (fields result.After).Finance)
                        CaseProgress.Decided
                        "Progress derives from remaining facts")
            testCase
                "stale or foreign context is an integrity failure and missing entry is a refusal"
                (fun () ->
                    let start = initial ()
                    let before = Some start
                    let act = CaseAct.RecordPayment(payment "1" "2026-01-11")
                    let foreign = CaseId.create (id 99) |> Option.get

                    for context in
                        [
                            CasePaymentContext.NoneRequired
                            CasePaymentContext.Recording(foreign, start.Revision, false)
                            CasePaymentContext.Recording(start.CaseId, Revision.first, false)
                        ] do
                        Expect.equal
                            (run 3 context before act)
                            (Error(
                                CaseDecisionError.InvalidContext
                                    RecordViolation.InvalidPaymentContext
                            ))
                            "Facts must belong to the acted-on case version"

                    let paid =
                        run
                            3
                            (CasePaymentContext.Recording(start.CaseId, start.Revision, false))
                            before
                            act
                        |> accepted

                    let requested = OperationId.create (id 99) |> Option.get

                    let missing =
                        run
                            4
                            (CasePaymentContext.Removing(
                                paid.After.CaseId,
                                paid.After.Revision,
                                None,
                                None
                            ))
                            (Some paid.After)
                            (CaseAct.RemovePayment(OperationId.value requested))

                    Expect.equal
                        missing
                        (Error(CaseDecisionError.Refused(Refusal.Case CaseRefusal.PaymentNotFound)))
                        "Unknown entry is a business refusal after case eligibility")
            testCase "reserved payment identity never produces a changed entry" (fun () ->
                let current = initial ()

                Expect.equal
                    (run
                        3
                        (CasePaymentContext.Recording(current.CaseId, current.Revision, true))
                        (Some current)
                        (CaseAct.RecordPayment(payment "1" "2026-01-11")))
                    (Error(CaseDecisionError.Refused Refusal.IdempotencyConflict))
                    "Removed identities cannot be reused when the store reports their reservation")
        ]
