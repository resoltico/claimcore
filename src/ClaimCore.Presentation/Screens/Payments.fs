namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
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

    let tableWithControls select controls (position: FinancialPosition) entries =
        let totals = FinancialPosition.totals position

        let amount row =
            let entry = select row

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
                    (select >> PaymentEntry.date >> PresentedValue.Date)
                column "paidAmount" "field.paidAmount.label" Prominence.Primary true amount
            ]

        let table =
            ScreenEntries.tableWithControls
                (select >> PaymentEntry.id >> OperationId.format)
                (fun row ->
                    let entry = select row

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
                controls
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

    let table position entries =
        tableWithControls id (fun _ -> None, []) position entries

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
                        (RegionContent.Table(
                            tableWithControls
                                (fun (item: CasePaymentItem) -> item.Entry)
                                (fun item ->
                                    None,
                                    TaskAffordances.paymentRemove value.CaseId value.Revision item)
                                position
                                value.Items
                        ))
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
