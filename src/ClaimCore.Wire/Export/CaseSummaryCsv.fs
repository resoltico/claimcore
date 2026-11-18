namespace ClaimCore.Wire.Export

open System.Globalization
open ClaimCore.Domain
open ClaimCore.Application.Contracts

module CaseSummaryCsv =
    let header =
        Csv.row
            [
                "group"
                "count"
                "currency"
                "claimed"
                "claimedCases"
                "payable"
                "payableCases"
                "netPaid"
                "netPaidCases"
            ]

    let private count (value: int64) =
        value.ToString(CultureInfo.InvariantCulture)

    let private money (value: CurrencyTotal option) =
        match value with
        | Some value -> [ CsvValue.ExactTotal value.Amount; CsvValue.Text(count value.Cases) ]
        | None -> [ CsvValue.Text ""; CsvValue.Text "" ]

    let private groupRows label total claimed payable netPaid =
        CaseMoneyRow.rows claimed payable netPaid
        |> Seq.map (fun row ->
            let labels =
                [
                    label
                    count total
                    row.Currency |> Option.map Currency.format |> Option.defaultValue ""
                ]
                |> List.map CsvValue.Text

            labels @ money row.Claimed @ money row.Payable @ money row.NetPaid
            |> Csv.typedRow)

    let chunks (summary: CaseWorklistSummary) =
        match summary.GroupBy with
        | None ->
            groupRows
                "ALL_CASES"
                summary.Counts.Total
                summary.Claimed
                summary.Payable
                summary.NetPaid
        | Some _ ->
            summary.Groups
            |> Seq.collect (fun group ->
                groupRows
                    (CaseGroupKey.token group.Group)
                    group.Count
                    group.Claimed
                    group.Payable
                    group.NetPaid)

    let rows summary =
        chunks summary |> Seq.toArray |> Array.concat
