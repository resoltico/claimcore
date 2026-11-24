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

    let netPaid (totals: PaymentTotals) =
        PresentedValue.moneyTotal (Money.currencyValue totals.Payable) totals.NetPaid

    let progressCaptionId = "label.paymentProgress"

    let meter (position: FinancialPosition) =
        FinancialPosition.totals position
        |> Option.bind (fun totals ->
            let payable = DecimalTotal.ofAmount (Money.amount totals.Payable)

            if totals.NetPaid < DecimalTotal.zero || totals.NetPaid > payable then
                None
            else
                let current =
                    DecimalTotal.format totals.NetPaid
                    |> Amount.parse
                    |> Result.defaultWith (fun _ ->
                        invalidOp "Net payment lies within payable bounds.")
                    |> fun amount -> Money.create amount (Money.currencyValue totals.Payable)

                Some
                    {
                        PresentedMoneyMeter.Current = money current
                        Target = money totals.Payable
                        Complete =
                            CaseProgress.ofFinancial position |> CaseProgress.completesPayment
                        Description =
                            {
                                MessageId = progressCaptionId
                                Arguments =
                                    Map.ofList
                                        [
                                            "paid", PresentedValue.money current
                                            "payable", PresentedValue.money totals.Payable
                                        ]
                            }
                    })

    let tableWithControls
        select
        controls
        (position: FinancialPosition)
        (payees: PaymentPayee list)
        entries
        =
        let totals = FinancialPosition.totals position

        let definitions =
            [
                column
                    "paymentDate"
                    "field.paymentDate.label"
                    Prominence.Primary
                    false
                    (select >> PaymentEntry.date >> PresentedValue.Date)
                column
                    "paidAmount"
                    "field.paidAmount.label"
                    Prominence.Primary
                    true
                    (select >> PaymentEntry.money >> PresentedValue.money)
                column
                    "direction"
                    "field.direction.label"
                    Prominence.Secondary
                    false
                    (select >> PaymentEntry.direction >> code PaymentDirection.vocabulary)
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
                                        "label.technicalDetails"
                                        [
                                            value
                                                "reference"
                                                "field.reference.label"
                                                Prominence.Detail
                                                (PaymentEntry.reference entry
                                                 |> optional (
                                                     Identifier.format >> PresentedValue.Identifier
                                                 ))
                                            match PaymentEntry.payee entry with
                                            | None -> ()
                                            | Some party ->
                                                let payee =
                                                    payees
                                                    |> List.tryFind (fun payee -> payee.Id = party)

                                                value
                                                    "payee"
                                                    "field.payee.label"
                                                    Prominence.Detail
                                                    (match payee with
                                                     | Some payee ->
                                                         optional PresentedValue.Party payee.Name
                                                     | None ->
                                                         PresentedValue.Identifier(
                                                             PartyId.format party
                                                         ))

                                                if payee |> Option.exists _.Removed then
                                                    value
                                                        "payeeState"
                                                        "field.payee.label"
                                                        Prominence.Detail
                                                        (PresentedValue.Message
                                                            "label.removedPayee")
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

        let decision =
            section
                "label.paymentDecision"
                ([
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
                        "decisionBasis"
                        "field.decisionBasis.label"
                        Prominence.Detail
                        (position.Decision
                         |> Option.bind Decision.basis
                         |> optional (NoteText.format >> PresentedValue.Narrative))
                 ]
                 |> List.map withRecorded)

        let paymentTotals =
            section
                "label.paymentTotals"
                [
                    value
                        "paymentCount"
                        "label.payments"
                        Prominence.Primary
                        (PresentedValue.Count position.PaymentCount)
                    for KeyValue(currency, buckets) in position.CurrencyTotals do
                        value
                            ("netPaid." + Currency.format currency)
                            "label.netPaid"
                            Prominence.Primary
                            (PresentedValue.moneyTotal currency (CurrencyPaymentTotals.net buckets))
                ]

        { table with
            Meter = meter position
            Sections =
                [
                    paymentTotals
                    { decision with
                        EmptyMessageId = Some "empty.paymentDecision"
                    }
                ]
        }

    let table position entries =
        tableWithControls id (fun _ -> None, []) position [] entries

    /// Receipts retain the direction and identity of every accepted payment change.
    let changes position differences =
        let table =
            tableWithControls PaymentDifference.entry (fun _ -> None, []) position [] differences

        { table with
            Columns =
                table.Columns
                @ [
                    {
                        Key = "change"
                        LabelId = "label.action"
                        Prominence = Prominence.Primary
                        IsMoney = false
                        Recorded = true
                        Joins = None
                    }
                    {
                        Key = "paymentId"
                        LabelId = "field.paymentId.label"
                        Prominence = Prominence.Primary
                        IsMoney = false
                        Recorded = true
                        Joins = None
                    }
                ]
            Rows =
                List.map2
                    (fun row difference ->
                        let direction =
                            match difference with
                            | PaymentDifference.Recorded _ -> "task.case.recordPayment.done"
                            | PaymentDifference.Removed _ -> "task.case.removePayment.done"

                        { row with
                            Values =
                                row.Values
                                @ [
                                    PresentedValue.Message direction
                                    PresentedValue.Identifier(
                                        difference
                                        |> PaymentDifference.entry
                                        |> PaymentEntry.id
                                        |> OperationId.format
                                    )
                                ]
                        })
                    table.Rows
                    differences
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
                        (RegionContent.Table(
                            tableWithControls
                                (fun (item: CasePaymentItem) -> item.Entry)
                                (fun item ->
                                    None,
                                    TaskAffordances.paymentEntryActions
                                        value.CaseId
                                        value.Revision
                                        item)
                                position
                                value.Payees
                                value.Items
                        ))
                    |> ScreenEntries.withCount "payments" position.PaymentCount
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
