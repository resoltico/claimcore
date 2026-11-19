namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module CaseTotalsScreen =
    let caseTotals (reply: CaseWorklistSummary) =
        let group title vocabulary (items: ValueCount<'value> list) =
            section
                title
                (items
                 |> List.map (fun item ->
                     value
                         (Vocabulary.token vocabulary item.Value)
                         (Vocabulary.messageKey vocabulary item.Value "label")
                         Prominence.Primary
                         (PresentedValue.Count item.Count)))

        let totals title (items: CurrencyTotal list) =
            section
                title
                (items
                 |> List.collect (fun item ->
                     let currency = Currency.format item.Currency

                     [
                         value
                             currency
                             "label.total"
                             Prominence.Primary
                             (PresentedValue.moneyTotal item.Currency item.Amount)
                         value
                             (currency + ".cases")
                             "label.contributingCases"
                             Prominence.Secondary
                             (PresentedValue.Count item.Cases)
                     ]))

        [
            section
                "label.cases"
                [
                    value
                        "total"
                        "label.total"
                        Prominence.Primary
                        (PresentedValue.Count reply.Counts.Total)
                ]
            group "input.assignment.label" CaseAssignment.vocabulary reply.Counts.Assignment
            group "input.due.label" CaseDue.vocabulary reply.Counts.Due
            group "input.lifecycle.label" Lifecycle.vocabulary reply.Counts.Lifecycles
            group "input.status.label" CaseStatus.vocabulary reply.Counts.Statuses
            group "input.progress.label" CaseProgress.vocabulary reply.Counts.Progress
            totals "label.claimedTotals" reply.Claimed
            totals "label.payableTotals" reply.Payable
            totals "label.paidTotals" reply.NetPaid
        ]


    let private groupValue =
        function
        | CaseGroupKey.Month month -> PresentedValue.Identifier(CaseMonth.format month)
        | CaseGroupKey.Outcome outcome -> code ClosureOutcome.vocabulary outcome
        | CaseGroupKey.Responsible account -> PresentedValue.Actor account
        | CaseGroupKey.DaysOpen band -> code DaysOpenBand.vocabulary band
        | CaseGroupKey.Missing group -> code MissingCaseGroup.vocabulary group

    let private groupRows (groups: CaseMoneyGroup list) =
        let amount (item: CurrencyTotal option) =
            item
            |> Option.map (fun total -> PresentedValue.moneyTotal total.Currency total.Amount)
            |> Option.defaultValue (PresentedValue.Absent "label.notRecorded")

        let count (item: CurrencyTotal option) =
            item |> Option.map _.Cases |> Option.defaultValue 0L |> PresentedValue.Count

        let columns =
            [
                column
                    "group"
                    "input.groupBy.label"
                    Prominence.Primary
                    false
                    (fun ((group: CaseMoneyGroup), _) -> groupValue group.Group)
                column "count" "label.cases" Prominence.Primary false (fun (group, _) ->
                    PresentedValue.Count group.Count)
                column
                    "currency"
                    "label.reportingCurrency"
                    Prominence.Secondary
                    false
                    (fun (_, (row: CaseMoneyRow)) ->
                        row.Currency
                        |> Option.map (fun currency ->
                            PresentedValue.Code("currency", Currency.format currency))
                        |> Option.defaultValue (PresentedValue.Absent "label.notRecorded"))
                column
                    "claimed"
                    "field.claimedAmount.label"
                    Prominence.Secondary
                    true
                    (fun (_, row) -> amount row.Claimed)
                column
                    "claimedCases"
                    "label.claimedCases"
                    Prominence.Secondary
                    false
                    (fun (_, row) -> count row.Claimed)
                column
                    "payable"
                    "field.payableAmount.label"
                    Prominence.Secondary
                    true
                    (fun (_, row) -> amount row.Payable)
                column
                    "payableCases"
                    "label.payableCases"
                    Prominence.Secondary
                    false
                    (fun (_, row) -> count row.Payable)
                column "netPaid" "label.netPaid" Prominence.Secondary true (fun (_, row) ->
                    amount row.NetPaid)
                column
                    "netPaidCases"
                    "label.netPaidCases"
                    Prominence.Secondary
                    false
                    (fun (_, row) -> count row.NetPaid)
            ]

        let key (group, row) =
            CaseGroupKey.token group.Group
            + ":"
            + (row.Currency |> Option.map Currency.format |> Option.defaultValue "")

        groups
        |> List.collect (fun group ->
            CaseMoneyRow.rows group.Claimed group.Payable group.NetPaid
            |> List.map (fun row -> group, row))
        |> List.chunkBySize Paging.MaximumPageSize
        |> List.map (fun rows ->
            region
                RegionRole.Results
                (Some "label.summaryGroups")
                (RegionContent.Table(table key (fun _ -> None) columns rows)))

    let view (reply: CaseWorklistSummary) =
        let values =
            caseTotals reply
            |> List.filter (fun section ->
                List.contains
                    section.TitleId
                    [
                        "label.cases"
                        "label.claimedTotals"
                        "label.payableTotals"
                        "label.paidTotals"
                    ])
            |> List.collect (fun section ->
                section.Entries
                |> List.map (fun entry ->
                    { entry with
                        Key = section.TitleId + "." + entry.Key
                        LabelId =
                            match entry.Value with
                            | PresentedValue.MoneyTotal _ -> section.TitleId
                            | _ -> entry.LabelId
                    }))

        Screen.create (
            [
                region
                    RegionRole.Notice
                    None
                    (RegionContent.Notice
                        [
                            {
                                MessageId = "notice.summaryCurrentFacts"
                                Reason = None
                                Tone = NoticeTone.Info
                            }
                        ])
                entryRegion RegionRole.Totals None values
            ]
            @ groupRows reply.Groups
        )
