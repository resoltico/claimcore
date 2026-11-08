namespace ClaimCore.Presentation

open ClaimCore.Domain
open ScreenEntries

module PaymentsScreen =
    let private money value =
        {
            PresentedMoney.Value = value
            DisplayScale = Money.displayScale value
        }

    let meter (progress: PaymentProgress) =
        PaymentProgress.totals progress
        |> Option.map (fun totals ->
            {
                PresentedMoneyMeter.Current = money totals.Paid
                Target = money totals.Payable
                Complete = CaseProgress.ofPayment progress = CaseProgress.Paid
                Description =
                    {
                        MessageId = "label.paymentProgress"
                        Arguments =
                            Map.ofList["paid", PresentedValue.money totals.Paid
                                       "payable", PresentedValue.money totals.Payable]
                    }
            })

    let table (fields: CaseFields) =
        let totals = PaymentProgress.totals fields.Progress

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
                (List.rev fields.Payments)

        { table with
            Meter = meter fields.Progress
            Sections =
                [
                    section
                        "label.paymentTotals"
                        [
                            value
                                "paymentDecisionDate"
                                "field.paymentDecisionDate.label"
                                Prominence.Primary
                                (fields.Decision
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
