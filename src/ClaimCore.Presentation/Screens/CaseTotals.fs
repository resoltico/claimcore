namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module CaseTotalsScreen =
    let private groupValue =
        function
        | CaseGroupKey.Month month -> PresentedValue.Identifier(CaseMonth.format month)
        | CaseGroupKey.Outcome outcome -> code ClosureOutcome.vocabulary outcome
        | CaseGroupKey.Responsible account -> PresentedValue.Actor account
        | CaseGroupKey.DaysOpen band -> code DaysOpenBand.vocabulary band
        | CaseGroupKey.Missing group -> code MissingCaseGroup.vocabulary group

    let private amount (item: CurrencyTotal option) =
        item
        |> Option.map (fun total -> PresentedValue.moneyTotal total.Currency total.Amount)
        |> Option.defaultValue (PresentedValue.Absent "label.notRecorded")

    let private count (item: CurrencyTotal option) =
        item
        |> Option.map _.Cases
        |> Option.map PresentedValue.Count
        |> Option.defaultValue (PresentedValue.Absent "label.notRecorded")

    let private currency (row: CaseMoneyRow) =
        row.Currency
        |> Option.map (fun code -> PresentedValue.Code("currency", Currency.format code))
        |> Option.defaultValue (PresentedValue.Absent "label.notRecorded")

    let private results (rows: CaseMoneyRow list) =
        let columns =
            [
                column "group" "input.groupBy.label" Prominence.Primary false (fun row ->
                    row.Group
                    |> Option.map groupValue
                    |> Option.defaultValue (PresentedValue.Message "label.cases"))
                column
                    "count"
                    "label.cases"
                    Prominence.Primary
                    false
                    (_.Count >> PresentedValue.Count)
                column "currency" "label.reportingCurrency" Prominence.Secondary false currency
                column
                    "claimed"
                    "field.claimedAmount.label"
                    Prominence.Secondary
                    true
                    (_.Claimed >> amount)
                column
                    "claimedCases"
                    "label.claimedCases"
                    Prominence.Secondary
                    false
                    (_.Claimed >> count)
                column
                    "payable"
                    "field.payableAmount.label"
                    Prominence.Secondary
                    true
                    (_.Payable >> amount)
                column
                    "payableCases"
                    "label.payableCases"
                    Prominence.Secondary
                    false
                    (_.Payable >> count)
                column "netPaid" "label.netPaid" Prominence.Secondary true (_.NetPaid >> amount)
                column
                    "netPaidCases"
                    "label.netPaidCases"
                    Prominence.Secondary
                    false
                    (_.NetPaid >> count)
            ]

        let key row =
            (row.Group |> Option.map CaseGroupKey.token |> Option.defaultValue "")
            + ":"
            + (row.Currency |> Option.map Currency.format |> Option.defaultValue "")

        region
            RegionRole.Results
            (Some "label.summaryGroups")
            (RegionContent.Table(table key (fun _ -> None) columns rows))

    let view (reply: CaseWorklistSummary) =
        Screen.create
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
                entryRegion
                    RegionRole.Totals
                    None
                    [
                        value
                            "total"
                            "label.cases"
                            Prominence.Primary
                            (PresentedValue.Count reply.Counts.Total)
                    ]
                results reply.Rows
            ]
