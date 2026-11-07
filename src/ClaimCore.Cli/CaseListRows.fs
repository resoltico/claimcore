namespace ClaimCore.Cli

open System.Globalization
open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation

module internal CaseListRows =
    [<NoEquality; NoComparison>]
    type private CsvColumn =
        {
            Key: string
            Value: WorkDateBasis -> CaseSummary -> string option
        }

    let private column key read =
        {
            Key = key
            Value = fun _ row -> read row
        }

    let private listedValue field (row: CaseSummary) =
        CaseState.fields row.State
        |> Option.bind (fun facts -> Map.tryFind field facts.Fields |> Option.flatten)
        |> Option.map CaseFieldValue.format

    let private columns =
        [
            column "caseReference" (fun row -> Some(CaseReference.format row.Reference))
            column "revision" (fun row -> Some(Revision.format row.Revision))
            column "state.kind" (fun row -> Some(CaseState.token row.State))
            column "headline" (fun row -> Some(CaseHeadline.token row.Headline))
            column "attention" (fun row ->
                Some(row.Attention |> List.map CaseAttention.token |> String.concat ";"))
            for field in CaseFields.declarations |> List.filter _.Listed do
                column ("state.fields." + field.Key) (listedValue field.Field)
            column "responsible.actorId" (fun row ->
                row.Responsible |> Option.map (fun actor -> ActorId.format actor.ActorId))
            column "responsible.displayName.value" (fun row ->
                row.Responsible
                |> Option.bind (fun actor ->
                    match actor.DisplayName with
                    | DisplayName.Named value -> Some(PartyName.format value)
                    | DisplayName.Erased -> None))
            column "responsible.displayName.kind" (fun row ->
                row.Responsible
                |> Option.map (fun actor ->
                    match actor.DisplayName with
                    | DisplayName.Named _ -> "NAMED"
                    | DisplayName.Erased -> "ERASED"))
            column "responsible.availability" (fun row ->
                row.Responsible
                |> Option.map (fun actor -> ActorAvailability.token actor.Availability))
            column "nextAction.reminderId" (fun row ->
                row.NextAction |> Option.map (fun next -> ReminderId.format next.ReminderId))
            column "nextAction.action" (fun row ->
                row.NextAction |> Option.map (fun next -> ReminderAction.format next.Action))
            column "nextAction.due" (fun row ->
                row.NextAction |> Option.map (fun next -> CalendarDate.format next.Due))
            column "nextAction.dueState" (fun row ->
                row.NextAction |> Option.map (fun next -> CaseDue.token next.DueState))
            column "pendingReminders" (fun row ->
                Some(row.PendingReminders.ToString(CultureInfo.InvariantCulture)))
            column "changedAt" (fun row -> Some(TextReplyFormat.instant row.ChangedAt))
            {
                Key = "basis.businessDate"
                Value =
                    fun basis _ ->
                        Some(CalendarDate.ofDate basis.BusinessDate |> CalendarDate.format)
            }
            {
                Key = "basis.timeZone"
                Value = fun basis _ -> Some basis.TimeZoneId
            }
        ]

    let private csv values =
        String.concat "," (values |> List.map CsvCell.format) + "\r\n"
        |> Encoding.UTF8.GetBytes

    let csvHeader = columns |> List.map _.Key |> csv

    let csvRows basis rows =
        rows
        |> List.map (fun row ->
            columns
            |> List.map (fun column -> column.Value basis row |> Option.defaultValue "")
            |> csv)
        |> Array.concat

    let textHeaderWith options language =
        TextTable.header language options Descriptor.caseSummaryColumns

    let textRowsWith options language rows =
        TextTable.rows language options Descriptor.caseSummaryColumns rows

    let textHeader language =
        textHeaderWith (TextTable.environment ()) language

    let textRows language rows =
        textRowsWith (TextTable.environment ()) language rows
