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

    let chunks (summary: CaseWorklistSummary) =
        summary.Rows
        |> Seq.map (fun row ->
            let labels =
                [
                    row.Group |> Option.map CaseGroupKey.token |> Option.defaultValue "ALL_CASES"
                    count row.Count
                    row.Currency |> Option.map Currency.format |> Option.defaultValue ""
                ]
                |> List.map CsvValue.Text

            labels @ money row.Claimed @ money row.Payable @ money row.NetPaid
            |> Csv.typedRow)

    let rows summary =
        chunks summary |> Seq.toArray |> Array.concat
