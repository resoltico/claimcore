module ClaimCore.TestSupport.PaymentEntriesContract

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected read: %A" other

let private execute fixture service number reference revision task values =
    Service.executeSubmission
        service
        (((request fixture number reference revision task values)))
        token

let private payment date amount currency =
    Submissions.fields
        [
            BusinessField.PaymentDate, date
            BusinessField.PaidAmount, amount
            BusinessField.PaidCurrency, currency
        ]

let private decision amount currency =
    DomainInputs.decisionFrom
        [ BusinessField.PayableAmount, amount; BusinessField.PayableCurrency, currency ]
    |> DecisionInput.values
    |> Submissions.fields

let private selected =
    { CaseListFilter.empty<string> with
        View = Some CaseView.DecidedUnpaid
    }

let internal lifecycle fixture =
    task {
        let! owner = caseWorker fixture

        let! grant =
            grant
                owner
                (id fixture 20)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople grant

        let! opened =
            execute
                (fixture)
                (owner)
                (10)
                ("PAY-EUR")
                (0L)
                "case.open"
                (ClaimCore.TestSupport.Submissions.fields (
                    RegistrationInput.values (
                        (DomainInputs.registrationWith
                            [
                                BusinessField.ClaimedAmount, "100"
                                BusinessField.ClaimedCurrency, "EUR"
                            ]
                            DomainInputs.registration)
                    )
                ))

        let receipt = accepted opened

        let caseId =
            match receipt.Subject with
            | ReceiptSubject.Case case -> case.CaseId
            | _ -> failtest "Case receipt required"

        let! decided =
            execute fixture owner 11 "PAY-EUR" 1L "case.decide" (decision "100" "EUR")

        accepted decided |> ignore

        let! first =
            execute
                fixture
                owner
                12
                "PAY-EUR"
                2L
                "case.recordPayment"
                (payment "2026-08-20" "25" "EUR")

        accepted first |> ignore

        let! second =
            execute
                fixture
                owner
                13
                "PAY-EUR"
                3L
                "case.recordPayment"
                (payment "2026-08-21" "35.25" "EUR")

        accepted second |> ignore

        let! dollars =
            execute
                (fixture)
                (owner)
                (30)
                ("PAY-USD")
                (0L)
                "case.open"
                (ClaimCore.TestSupport.Submissions.fields (
                    RegistrationInput.values (
                        (DomainInputs.registrationWith
                            [
                                BusinessField.ClaimedAmount, "50"
                                BusinessField.ClaimedCurrency, "USD"
                            ]
                            DomainInputs.registration)
                    )
                ))

        accepted dollars |> ignore

        let! dollarDecision =
            execute fixture owner 31 "PAY-USD" 1L "case.decide" (decision "50" "USD")

        accepted dollarDecision |> ignore

        let! dollarPayment =
            execute
                fixture
                owner
                32
                "PAY-USD"
                2L
                "case.recordPayment"
                (payment "2026-08-20" "5" "USD")

        accepted dollarPayment |> ignore

        let! page =
            Service.list
                owner
                {
                    Page = { AfterCursor = None; Limit = 50 }
                    Filter = selected
                }
                token

        let page = read page

        Expect.equal
            (page.Items |> List.map (_.Reference >> CaseReference.format))
            [ "PAY-EUR"; "PAY-USD" ]
            "Partly paid cases remain in decided, not fully paid"

        let euro = page.Items.Head

        Expect.equal
            (euro.NetPaid |> Option.map (fun totals -> DecimalTotal.format totals.NetPaid))
            (Some "60.25")
            "List data carries the exact independent paid sum"

        Expect.equal euro.PaymentCount 2L "Both entries are counted"

        Expect.equal
            (euro.LastPaymentDate |> Option.map CalendarDate.format)
            (Some "2026-08-21")
            "The latest payment date is retained"

        Expect.isFalse
            (List.contains CaseAttention.ReadyToClose euro.Attention)
            "Partial payment is not ready to close"

        let! totals = Service.caseSummary owner (CaseSummaryRequest.ungrouped selected) token
        let totals = read totals

        Expect.equal
            (totals.Rows
             |> List.choose _.NetPaid
             |> List.map (fun total ->
                 Currency.format total.Currency, DecimalTotal.format total.Amount))
            [ "EUR", "60.25"; "USD", "5" ]
            "SQL totals keep currencies separate"

        let! removed =
            execute
                (fixture)
                (owner)
                (14)
                ("PAY-EUR")
                (4L)
                "case.removePayment"
                (Map.ofList
                    [
                        TaskInputId.Target InputTarget.PaymentId,
                        ClaimCore.Application.Contracts.SubmissionValue.Text(
                            ((id fixture 12)).ToString("D")
                        )
                    ])

        accepted removed |> ignore

        let! full =
            execute
                fixture
                owner
                15
                "PAY-EUR"
                5L
                "case.recordPayment"
                (payment "2026-08-22" "64.75" "EUR")

        accepted full |> ignore
        let! current = Service.get owner { Target = CaseSelector.Id caseId } token
        let current = read current

        Expect.contains
            current.Attention
            CaseAttention.ReadyToClose
            "Exact full payment reaches ready to close"

        let! remaining =
            Service.list
                owner
                {
                    Page = { AfterCursor = None; Limit = 50 }
                    Filter = selected
                }
                token

        Expect.equal
            ((read remaining).Items |> List.map (_.Reference >> CaseReference.format))
            [ "PAY-USD" ]
            "Fully paid EUR leaves the selection"

        let! erased =
            execute (fixture) (owner) (18) ("PAY-EUR") (6L) "case.erase" (Map.empty)

        accepted erased |> ignore
        let! erasedCase = Service.get owner { Target = CaseSelector.Id caseId } token

        Expect.equal
            (read erasedCase).Snapshot.State
            CaseState.Erased
            "Erasure removes payment facts"

        let! after =
            Service.caseSummary
                owner
                (CaseSummaryRequest.ungrouped CaseListFilter.empty<string>)
                token

        Expect.equal
            ((read after).Rows
             |> List.choose _.NetPaid
             |> List.map (fun total ->
                 Currency.format total.Currency, DecimalTotal.format total.Amount))
            [ "USD", "5" ]
            "Erased paid amounts disappear from totals"
    }
