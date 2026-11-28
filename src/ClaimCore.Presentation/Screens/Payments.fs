namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module PaymentsScreen =
    let progressCaptionId = "label.paymentProgress"

    [<Literal>]
    let ProgressMaximum = 1000

    let private meter (assessment: FinancialAssessment) =
        if assessment.NeedsReview then
            None
        else
            assessment.Comparison
            |> Option.bind (fun comparison ->
                let payable = DecimalTotal.ofAmount (Money.amount comparison.Payable)

                if payable = DecimalTotal.zero then
                    None
                else
                    Some
                        {
                            PresentedMoneyMeter.Position =
                                DecimalTotal.progress ProgressMaximum comparison.NetPaid payable
                            Maximum = ProgressMaximum
                            Description =
                                {
                                    MessageId = progressCaptionId
                                    Arguments =
                                        Map.ofList
                                            [
                                                "paid",
                                                PresentedValue.moneyTotal
                                                    (Money.currencyValue comparison.Payable)
                                                    comparison.NetPaid
                                                "payable", PresentedValue.money comparison.Payable
                                            ]
                                }
                        })

    let tableWithControls
        select
        controls
        (position: FinancialPosition)
        outcome
        (payees: PaymentPayee list)
        entries
        =
        let assessment = FinancialAssessment.ofPosition outcome position

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
                        (assessment.Comparison
                         |> Option.map _.Payable
                         |> optional PresentedValue.money)
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
                    for KeyValue(currency, net) in FinancialPosition.actualBalances position do
                        value
                            ("netPaid." + Currency.format currency)
                            "label.netPaid"
                            Prominence.Primary
                            (PresentedValue.moneyTotal currency net)
                    match assessment.Overpayment with
                    | Some(currency, excess) ->
                        value
                            "overpayment"
                            "label.paymentOverpayment"
                            Prominence.Primary
                            (PresentedValue.moneyTotal currency excess)
                    | None -> ()
                    for KeyValue(currency, net) in assessment.NegativeBalances do
                        value
                            ("negativeBalance." + Currency.format currency)
                            "label.paymentNegativeBalance"
                            Prominence.Primary
                            (PresentedValue.moneyTotal currency net)
                    if assessment.SettledWithoutCoverage then
                        value
                            "settledWithoutCoverage"
                            "label.settledWithoutCoverage"
                            Prominence.Primary
                            (PresentedValue.Message "label.paymentsNeedReview")
                    match assessment.Comparison with
                    | Some comparison when PayableComparison.noPaymentDue comparison ->
                        value
                            "coverage"
                            "label.paymentCoverage"
                            Prominence.Primary
                            (PresentedValue.Message "label.noPaymentDue")
                    | _ -> ()
                ]

        { table with
            Meter = meter assessment
            Sections =
                [
                    paymentTotals
                    { decision with
                        EmptyMessageId = Some "empty.paymentDecision"
                    }
                ]
        }

    let table position entries =
        tableWithControls id (fun _ -> None, []) position None [] entries

    /// Receipts retain the direction and identity of every accepted payment change.
    let changes position (balances: BalanceDifference list) differences =
        let table =
            tableWithControls
                PaymentDifference.entry
                (fun _ -> None, [])
                position
                None
                []
                differences

        { table with
            Meter = None
            Sections =
                table.Sections
                @ (balances
                   |> List.map (fun balance ->
                       section
                           "label.netPaid"
                           [
                               value
                                   ("balance." + Currency.format balance.Currency + ".before")
                                   "label.before"
                                   Prominence.Secondary
                                   (optional
                                       (PresentedValue.moneyTotal balance.Currency)
                                       balance.Net.Before)
                               value
                                   ("balance." + Currency.format balance.Currency + ".after")
                                   "label.after"
                                   Prominence.Primary
                                   (optional
                                       (PresentedValue.moneyTotal balance.Currency)
                                       balance.Net.After)
                           ]))
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
                                value.ClosureOutcome
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

    let document (value: CasePaymentPage) =
        HumanDocument.snapshot
            value.Basis
            (page value)
            (DocumentSubject.Case(value.CaseId, value.Revision, value.WorkRevision))
            []
            []
            value.NextCursor
            (if CaseState.lifecycle value.State = Lifecycle.Erased then
                 HumanDisclosure.Erased
             else
                 HumanDisclosure.Current)
            None
