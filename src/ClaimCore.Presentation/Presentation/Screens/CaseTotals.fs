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
                 |> List.map (fun item ->
                     value
                         (Currency.format item.Currency)
                         "label.total"
                         Prominence.Primary
                         (PresentedValue.moneyTotal item.Currency item.Amount)))

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
        ]


    let view (reply: CaseWorklistSummary) =
        let values =
            caseTotals reply
            |> List.filter (fun section ->
                List.contains
                    section.TitleId
                    [ "label.cases"; "label.claimedTotals"; "label.payableTotals" ])
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

        Screen.create [ entryRegion RegionRole.Totals None values ]
