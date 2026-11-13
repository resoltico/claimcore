namespace ClaimCore.Presentation

open ClaimCore.Domain
open ScreenEntries

module PaymentsScreen =
    let private money value =
        {
            PresentedMoney.Value = value
            DisplayScale = Money.displayScale value
        }

    let meter (position: FinancialPosition) =
        FinancialPosition.totals position
        |> Option.map (fun totals ->
            {
                PresentedMoneyMeter.Current = money totals.Paid
                Target = money totals.Payable
                Complete = CaseProgress.ofFinancial position = CaseProgress.Paid
                Description =
                    {
                        MessageId = "label.paymentProgress"
                        Arguments =
                            Map.ofList["paid", PresentedValue.money totals.Paid
                                       "payable", PresentedValue.money totals.Payable]
                    }
            })

    let table (position: FinancialPosition) (entries: PaymentEntry list) =
        let totals = FinancialPosition.totals position

        let amount entry =
            match totals with
            | Some totals ->
                PresentedValue.money (
                    Money.create (PaymentEntry.amount entry) (Money.currencyValue totals.Payable)
                )
            | None -> invalidOp "Payment entries require a decision."

        let definitions =
            [
                column
                    "paymentDate"
                    "field.paymentDate.label"
                    Prominence.Primary
                    false
                    (PaymentEntry.date >> PresentedValue.Date)
                column "paidAmount" "field.paidAmount.label" Prominence.Primary true amount
            ]

        let table =
            ScreenEntries.table
                (PaymentEntry.id >> OperationId.format)
                (fun entry ->
                    Some
                        {
                            Summary = None
                            Highlights = []
                            Sections =
                                [
                                    section
                                        "label.recordDetails"
                                        [
                                            value
                                                "paymentId"
                                                "field.paymentId.label"
                                                Prominence.Detail
                                                (PresentedValue.Identifier(
                                                    OperationId.format (PaymentEntry.id entry)
                                                ))
                                        ]
                                ]
                        })
                definitions
                entries

        { table with
            Meter = meter position
            Sections =
                [
                    section
                        "label.paymentTotals"
                        [
                            value
                                "paymentCount"
                                "label.payments"
                                Prominence.Primary
                                (PresentedValue.Count position.PaymentCount)
                            value
                                "paymentDecisionDate"
                                "field.paymentDecisionDate.label"
                                Prominence.Primary
                                (position.Decision
                                 |> Option.map Decision.date
                                 |> optional PresentedValue.Date)
                            value
                                "payable"
                                "field.payableAmount.label"
                                Prominence.Primary
                                (totals |> Option.map _.Payable |> optional PresentedValue.money)
                            value
                                "paid"
                                "label.paid"
                                Prominence.Primary
                                (totals |> Option.map _.Paid |> optional PresentedValue.money)
                            value
                                "remaining"
                                "label.remaining"
                                Prominence.Primary
                                (totals |> Option.map _.Remaining |> optional PresentedValue.money)
                        ]
                ]
        }

    let page (value: ClaimCore.Application.Contracts.CasePaymentPage) =
        let body =
            match CaseState.fields value.State with
            | None ->
                [
                    ScreenEntries.entryRegion
                        RegionRole.Facts
                        None
                        [
                            ScreenEntries.value
                                "lifecycle"
                                "input.lifecycle.label"
                                Prominence.Primary
                                (PresentedValue.Message "lifecycle.ERASED.label")
                        ]
                ]
            | Some position ->
                [
                    ScreenEntries.region
                        RegionRole.Payments
                        (Some "label.payments")
                        (RegionContent.Table(table position (value.Items |> List.map _.Entry)))
                ]

        Screen.create (
            [
                ScreenEntries.entryRegion
                    RegionRole.Identity
                    None
                    [
                        ScreenEntries.value
                            "caseReference"
                            "field.caseReference.label"
                            Prominence.Primary
                            (PresentedValue.Reference value.Reference)
                    ]
            ]
            @ body
        )
