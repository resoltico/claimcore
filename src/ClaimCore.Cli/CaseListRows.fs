namespace ClaimCore.Cli

open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol

module internal CaseListRows =
    [<NoEquality; NoComparison>]
    type private Column =
        {
            Key: string
            Label: string
            Value: CaseSummary -> string option
        }

    let private live (get: CaseSummaryFacts -> string) (row: CaseSummary) =
        CaseState.fields row.State |> Option.map get

    let private optional (get: CaseSummaryFacts -> string option) (row: CaseSummary) =
        CaseState.fields row.State |> Option.bind get

    let private field name = FieldPresentation.labelId name

    let private columns =
        [
            {
                Key = "caseReference"
                Label = field BusinessField.CaseReference
                Value = fun row -> Some(CaseReference.format row.Reference)
            }
            {
                Key = "revision"
                Label = CliMessages.key CliMessage.RevisionLabel
                Value = fun row -> Some(Revision.format row.Revision)
            }
            {
                Key = "state.kind"
                Label = CliMessages.key CliMessage.StateLabel
                Value = fun row -> Some(CaseState.token row.State)
            }
            {
                Key = "state.status"
                Label = field BusinessField.Status
                Value = live (fun facts -> CaseStatus.token facts.Status)
            }
            {
                Key = "state.claimantName"
                Label = field BusinessField.ClaimantName
                Value = live (fun facts -> PartyName.format facts.ClaimantName)
            }
            {
                Key = "state.insurerName"
                Label = field BusinessField.InsurerName
                Value = live (fun facts -> PartyName.format facts.InsurerName)
            }
            {
                Key = "state.claimed.amount"
                Label = field BusinessField.ClaimedAmount
                Value = live (fun facts -> Money.format facts.Claimed)
            }
            {
                Key = "state.claimed.currency"
                Label = field BusinessField.ClaimedCurrency
                Value = live (fun facts -> Money.currency facts.Claimed)
            }
            {
                Key = "state.payable.amount"
                Label = field BusinessField.PayableAmount
                Value = optional (fun facts -> facts.Payable |> Option.map Money.format)
            }
            {
                Key = "state.payable.currency"
                Label = field BusinessField.PayableCurrency
                Value = optional (fun facts -> facts.Payable |> Option.map Money.currency)
            }
            {
                Key = "state.progress"
                Label = "input.progress.label"
                Value = live (fun facts -> CaseProgress.token facts.Progress)
            }
            {
                Key = "state.incidentNotificationDate"
                Label = field BusinessField.IncidentNotificationDate
                Value = live (fun facts -> CalendarDate.format facts.IncidentNotificationDate)
            }
            {
                Key = "changedAt"
                Label = "label.changedAt"
                Value = fun row -> Some(TextReplyFormat.instant row.ChangedAt)
            }
        ]

    let private csv values =
        String.concat "," (values |> List.map CsvCell.format) + "\r\n"
        |> Encoding.UTF8.GetBytes

    let csvHeader = columns |> List.map _.Key |> csv

    let csvRows rows =
        rows
        |> List.map (fun row ->
            columns
            |> List.map (fun column -> column.Value row |> Option.defaultValue "")
            |> csv)
        |> Array.concat

    let textHeader language =
        columns
        |> List.map (fun column -> TextReplyFormat.label language column.Label)
        |> String.concat "  "

    let textRows language (rows: CaseSummary list) =
        rows
        |> List.map (fun row ->
            columns
            |> List.map (fun column ->
                match column.Key, row.State with
                | "state.kind", _ ->
                    TextReplyFormat.term
                        language
                        Lifecycle.vocabulary
                        (CaseState.lifecycle row.State)
                | "state.status", (CaseState.Active facts | CaseState.Voided facts) ->
                    TextReplyFormat.term language CaseStatus.vocabulary facts.Status
                | "state.progress", (CaseState.Active facts | CaseState.Voided facts) ->
                    TextReplyFormat.term language CaseProgress.vocabulary facts.Progress
                | _ -> TextReplyFormat.displayed language (column.Value row))
            |> String.concat "  ")
