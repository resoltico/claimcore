module ClaimCore.Tests.PaymentEntriesTests

open System
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Tests.PropertyHarness
open ClaimCore.Domain

let private today = DateOnly(2026, 11, 8)

let private accepted =
    function
    | Ok value -> value
    | Error error -> failtestf "Expected acceptance, got %A" error

let private operation (number: int) =
    Guid.Parse("00000000-0000-7000-8000-" + number.ToString("x12"))

let private fields record =
    (Case.snapshot record).State |> CaseState.fields |> Option.get

let private sendOn date number act previous =
    Case.parse
        {
            OperationId = operation number
            CaseReference = "PAYMENTS"
            ExpectedRevision =
                previous
                |> Option.map (Case.snapshot >> _.Revision >> Revision.value)
                |> Option.defaultValue 0L
            Act = act
        }
    |> Result.bind (fun command -> Case.decide date command previous)

let private send number act previous = sendOn today number act (Some previous)

let private decision amount currency =
    DecisionInput.create
        [
            BusinessField.PaymentDecisionDate, "2026-09-01"
            BusinessField.PayableAmount, amount
            BusinessField.PayableCurrency, currency
        ]
    |> accepted
    |> CaseAct.Decide

let private payment date amount currency =
    PaymentInput.create
        [
            BusinessField.PaymentDate, date
            BusinessField.PaidAmount, amount
            BusinessField.PaidCurrency, currency
        ]
    |> accepted
    |> CaseAct.RecordPayment

let private opened () =
    let registration =
        RegistrationInput.create
            [
                BusinessField.IncidentDate, "2026-08-01"
                BusinessField.IncidentNotificationDate, "2026-08-02"
                BusinessField.IncidentCountry, "LV"
                BusinessField.ClaimantName, "Payment test claimant"
                BusinessField.InsurerName, "Payment test insurer"
                BusinessField.ClaimedAmount, "100"
                BusinessField.ClaimedCurrency, "EUR"
            ]
        |> accepted

    sendOn today 1 (CaseAct.Open registration) None |> accepted

let private decided () =
    opened () |> send 2 (decision "100" "EUR") |> accepted

let private partlyPaid () =
    decided () |> send 3 (payment "2026-09-07" "40" "EUR") |> accepted

let private correction amount currency : AuthoredCaseAct =
    CaseAct.Correct
        {
            Registration = Amendment.Keep
            Decision =
                Change.Update
                    {
                        Date = None
                        Payable = Some { Amount = amount; Currency = currency }
                    }
        }

let private positiveError result =
    match result with
    | Error(Refusal.InvalidInput issues) ->
        Expect.isTrue
            (InputIssues.toList issues
             |> List.exists (fun issue ->
                 issue.Target = InputTarget.Field BusinessField.PaidAmount
                 && issue.Violation = Violation.PaymentAmountPositive))
            "Zero is refused at the paid amount"
    | value -> failtestf "Expected a paid amount issue, got %A" value

[<Tests>]
let tests =
    testList
        "payment entries"
        [
            testCase
                "instalments retain accepting identities and reach paid only at the exact sum"
                (fun () ->
                    let first = partlyPaid ()

                    Expect.equal
                        (CaseProgress.ofPayment (fields first).Progress)
                        CaseProgress.PartlyPaid
                        "40 of 100 is partly paid"

                    let entry = (fields first).Payments |> List.exactlyOne

                    Expect.equal
                        (OperationId.value (PaymentEntry.id entry))
                        (operation 3)
                        "Accepting operation is the payment identity"

                    Expect.equal
                        (Amount.value (PaymentEntry.amount entry))
                        40M
                        "The recorded amount is retained"

                    let paid = first |> send 4 (payment "2026-09-08" "60" "EUR") |> accepted

                    Expect.equal
                        (CaseProgress.ofPayment (fields paid).Progress)
                        CaseProgress.Paid
                        "40 plus 60 pays exactly 100"

                    Expect.equal (fields paid).Payments.Length 2 "Both entries remain")
            testCase
                "zero amounts currency mismatches overpayments and future dates are refused"
                (fun () ->
                    let current = decided ()
                    send 3 (payment "2026-09-07" "0" "EUR") current |> positiveError

                    for act in
                        [
                            payment "2026-09-07" "1" "USD"
                            payment "2026-09-07" "100.0001" "EUR"
                            payment "2026-11-09" "1" "EUR"
                            payment "2026-08-31" "1" "EUR"
                        ] do
                        Expect.isError (send 3 act current) "The invalid entry is not accepted"

                    Expect.isEmpty
                        (fields current).Payments
                        "Refusals do not mutate accepted entries")
            testCase
                "fifty-first payment has a count input issue and removal frees capacity"
                (fun () ->
                    let mutable current = decided ()

                    for number in 3..52 do
                        current <- send number (payment "2026-09-07" "1" "EUR") current |> accepted

                    Expect.equal
                        (fields current).Payments.Length
                        50
                        "The declared bound is inclusive"

                    Expect.equal
                        ((fields current).Payments
                         |> List.sumBy (PaymentEntry.amount >> Amount.value))
                        50M
                        "The independent entry sum is exact"

                    let before = Case.snapshot current

                    match send 53 (payment "2026-09-07" "1" "EUR") current with
                    | Error(Refusal.InvalidInput issues) ->
                        Expect.equal
                            (InputIssues.toList issues
                             |> List.map (fun issue -> issue.Target, issue.Violation))
                            [
                                InputTarget.Group RecordGroup.Payment,
                                Violation.PaymentCountRange 50
                            ]
                            "The authored 51st entry has the required payment-group count diagnostic"
                    | other ->
                        failtestf "Expected count INVALID_INPUT, got %A" (Result.map ignore other)

                    Expect.equal
                        (Case.snapshot current)
                        before
                        "The refusal changes no accepted state"

                    for protectedState in
                        [
                            send 56 (CaseAct.Bare BareAct.Close) current |> accepted
                            send 58 (CaseAct.Void "Entry error") current |> accepted
                        ] do
                        match send 59 (payment "2026-09-07" "1" "EUR") protectedState with
                        | Error(Refusal.Case(CaseRefusal.NotOffered _)) -> ()
                        | other ->
                            failtestf
                                "Lifecycle protection precedes capacity admission: %A"
                                (Result.map ignore other)

                    let removed =
                        send
                            54
                            (CaseAct.RemovePayment(
                                (fields current).Payments.Head
                                |> PaymentEntry.id
                                |> OperationId.value
                            ))
                            current
                        |> accepted

                    Expect.isTrue
                        (Case.eligibleActs removed |> List.contains CaseActKind.RecordPayment)
                        "Removing an entry restores the recording offer"

                    let restored = send 55 (payment "2026-09-07" "1" "EUR") removed |> accepted

                    Expect.equal
                        (fields restored).Payments.Length
                        50
                        "Removal permits another accepted entry"

                    Expect.equal
                        ((fields restored).Payments
                         |> List.sumBy (PaymentEntry.amount >> Amount.value))
                        50M
                        "The independently checked total remains fifty euros"

                    Expect.isFalse
                        (Case.eligibleActs current |> List.contains CaseActKind.RecordPayment)
                        "The full collection offers no further recording")
            testCase
                "decide and correct preserve payments and cannot lower payable below their sum or change currency"
                (fun () ->
                    let current = partlyPaid ()

                    for act in
                        [
                            decision "39.9999" "EUR"
                            decision "100" "USD"
                            correction "39.9999" "EUR"
                            correction "100" "USD"
                        ] do
                        Expect.isError
                            (send 4 act current)
                            "A decision change cannot invalidate retained payments"

                    for act in [ decision "150" "EUR"; correction "150" "EUR" ] do
                        let changed = send 4 act current |> accepted

                        Expect.equal
                            (fields changed).Payments
                            (fields current).Payments
                            "The entries survive the decision change")
            testCase "withdrawal and clearing a corrected decision require no entries" (fun () ->
                let current = partlyPaid ()

                Expect.isError
                    (send 4 (CaseAct.Bare BareAct.WithdrawDecision) current)
                    "Withdrawal cannot orphan payments"

                Expect.isError
                    (send
                        4
                        (CaseAct.Correct
                            {
                                Registration = Amendment.Keep
                                Decision = Change.Clear
                            })
                        current)
                    "Correction cannot orphan payments"

                let removed = send 4 (CaseAct.RemovePayment(operation 3)) current |> accepted
                Expect.isEmpty (fields removed).Payments "Removal removes the selected entry"
                let withdrawn = send 5 (CaseAct.Bare BareAct.WithdrawDecision) removed |> accepted

                Expect.equal
                    (fields withdrawn).Progress
                    PaymentProgress.Undecided
                    "An empty decision can be withdrawn")
            testCase "zero payable with no entries is paid and can still be withdrawn" (fun () ->
                let zero = opened () |> send 2 (decision "0" "EUR") |> accepted

                Expect.equal
                    (CaseProgress.ofPayment (fields zero).Progress)
                    CaseProgress.Paid
                    "Zero paid equals zero payable"

                Expect.isEmpty (fields zero).Payments "No payment is invented"

                Expect.isOk
                    (send 3 (CaseAct.Bare BareAct.WithdrawDecision) zero)
                    "The no-entry withdrawal rule holds")
            testCase
                "an existing future date is not reused to admit a new entry after a calendar rollback"
                (fun () ->
                    let future =
                        sendOn
                            (DateOnly(2026, 11, 10))
                            3
                            (payment "2026-11-09" "1" "EUR")
                            (Some(decided ()))
                        |> accepted

                    Expect.isError
                        (send 4 (payment "2026-11-09" "1" "EUR") future)
                        "A new payment independently admits its date")
            testCase
                "payment recording and removal retain open-case and lifecycle restrictions"
                (fun () ->
                    let current = partlyPaid ()
                    let closed = send 4 (CaseAct.Bare BareAct.Close) current |> accepted
                    let voided = send 4 (CaseAct.Void "Entry made in error") current |> accepted

                    for state in [ closed; voided ] do
                        Expect.isError
                            (send 5 (payment "2026-09-08" "1" "EUR") state)
                            "An inactive payment write is refused"

                        Expect.isError
                            (send 5 (CaseAct.RemovePayment(operation 3)) state)
                            "Removal has the same lifecycle boundary")
            testCase
                "unknown payment identities and duplicate accepting identities cannot alter the collection"
                (fun () ->
                    let current = partlyPaid ()

                    Expect.isError
                        (send 4 (CaseAct.RemovePayment(operation 99)) current)
                        "Only a retained entry can be removed"

                    Expect.equal
                        (send 3 (payment "2026-09-08" "1" "EUR") current |> Result.map ignore)
                        (Error Refusal.IdempotencyConflict)
                        "The same accepting identity cannot create a second entry")
            testCase "erasure removes every payment entry with the case facts" (fun () ->
                let erased =
                    partlyPaid ()
                    |> send 4 (CaseAct.Bare BareAct.Erase)
                    |> accepted
                    |> Case.snapshot

                Expect.equal erased.State CaseState.Erased "No financial entries survive erasure")
        ]

let private checkSequence amounts replacement otherCurrency =
    let mutable current = decided ()
    let mutable expectedSum = 0M
    let mutable identities = []

    for index, amount in List.indexed amounts do
        let before = Case.snapshot current

        let result = send (index + 3) (payment "2026-09-07" (string amount) "EUR") current

        let allowed = identities.Length < 50 && expectedSum + decimal amount <= 100M

        Expect.equal
            (Result.isOk result)
            allowed
            "The independent entry count and sum determine recording admission"

        match result with
        | Ok next ->
            current <- next
            expectedSum <- expectedSum + decimal amount
            identities <- identities @ [ operation (index + 3) ]
        | Error _ ->
            Expect.equal (Case.snapshot current) before "A refusal changes no accepted state"

        let observed = fields current

        Expect.equal
            (observed.Payments |> List.sumBy (PaymentEntry.amount >> Amount.value))
            expectedSum
            "Every accepted amount contributes exactly once"

        Expect.equal
            (observed.Payments |> List.map (PaymentEntry.id >> OperationId.value))
            identities
            "Only accepting operations identify retained entries"

        Expect.isLessThanOrEqual expectedSum 100M "The payable bound always holds"

    Expect.equal
        (Result.isOk (send 1000 (CaseAct.Bare BareAct.WithdrawDecision) current))
        identities.IsEmpty
        "Only an entry-free decision can be withdrawn"

    let currency = if otherCurrency then "USD" else "EUR"

    let permitted =
        decimal replacement >= expectedSum && (identities.IsEmpty || not otherCurrency)

    let changed = send 1001 (decision (string replacement) currency) current

    Expect.equal
        (Result.isOk changed)
        permitted
        "Decision changes retain currency and the paid bound"

    changed
    |> Result.iter (fun next ->
        Expect.equal
            (fields next).Payments
            (fields current).Payments
            "A changed decision retains entries")

    let corrected = send 1002 (correction (string replacement) currency) current

    Expect.equal
        (Result.isOk corrected)
        (permitted && (replacement <> 100 || otherCurrency))
        "Corrections preserve entries and refuse no-ops"

    let emptied =
        identities
        |> List.indexed
        |> List.fold
            (fun state (index, id) ->
                send (1100 + index) (CaseAct.RemovePayment id) state |> accepted)
            current

    Expect.isEmpty (fields emptied).Payments "All selected entries are removed"

    Expect.isOk
        (send 1200 (CaseAct.Bare BareAct.WithdrawDecision) emptied)
        "Withdrawal is available after the last removal"

    let erased =
        send 1300 (CaseAct.Bare BareAct.Erase) current |> accepted |> Case.snapshot

    Expect.equal erased.State CaseState.Erased "Erasure retains no payment entries"
    true

[<Tests>]
let sequenceInvariants =
    testCase
        "payment entries generated sequences preserve bounds withdrawal decision changes and erasure"
        (fun () ->
            property {
                let! amounts = Gen.list (Range.constant 0 55) (Gen.int32 (Range.constant 1 3))
                let! replacement = Gen.int32 (Range.constant 0 150)
                let! otherCurrency = Gen.bool
                return checkSequence amounts replacement otherCurrency

            }
            |> run "payment entry sequences")
