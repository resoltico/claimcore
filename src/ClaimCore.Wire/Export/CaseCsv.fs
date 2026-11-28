namespace ClaimCore.Wire.Export

open System.Globalization
open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

module CaseCsv =
    [<NoEquality; NoComparison>]
    type private CsvColumn =
        {
            Key: string
            Value: WorkDateBasis -> CaseSummary -> CaseCurrencyContribution option -> CsvValue
        }

    let private column key read =
        {
            Key = key
            Value = fun _ row _ -> read row |> Option.defaultValue "" |> CsvValue.Text
        }

    let private parties role (row: CaseSummary) =
        CaseState.fields row.State
        |> Option.bind (fun facts -> Map.tryFind role facts.Parties)
        |> Option.map (List.map PartyName.format >> String.concat "; ")

    let private optionalAmount key read =
        {
            Key = key
            Value =
                fun _ _ contribution ->
                    contribution
                    |> Option.bind read
                    |> Option.map CsvValue.ExactTotal
                    |> Option.defaultValue (CsvValue.Text "")
        }

    let private columns =
        [
            column "caseId" (fun row -> Some(CaseId.format row.CaseId))
            column "caseReference" (fun row -> Some(CaseReference.format row.Reference))
            column "revision" (fun row -> Some(Revision.format row.Revision))
            column "workRevision" (fun row -> Some(Revision.format row.WorkRevision))
            column "state.kind" (fun row -> Some(CaseState.token row.State))
            column "headline" (fun row -> Some(CaseHeadline.token row.Headline))
            column "attention" (fun row ->
                Some(row.Attention |> List.map CaseAttention.token |> String.concat ";"))
            column "claimants" (parties PartyRole.Claimant)
            column "insurers" (parties PartyRole.Insurer)
            column "registeredOn" (fun row -> Some(CalendarDate.format row.Grouping.RegisteredOn))
            column "receivedOn" (fun row ->
                row.Grouping.ReceivedOn |> Option.map CalendarDate.format)
            column "closedOn" (fun row -> row.Grouping.ClosedOn |> Option.map CalendarDate.format)
            {
                Key = "daysOpen"
                Value = fun _ row _ -> CsvValue.Integer row.Grouping.DaysOpen
            }
            column "outcome" (fun row -> row.Grouping.Outcome |> Option.map ClosureOutcome.token)
            {
                Key = "currency"
                Value =
                    fun _ _ contribution ->
                        contribution
                        |> Option.map (_.Currency >> Currency.format)
                        |> Option.defaultValue ""
                        |> CsvValue.Text
            }
            optionalAmount "claimed" _.Claimed
            optionalAmount "payable" _.Payable
            optionalAmount "netPaid" _.NetPaid
            column "paymentCount" (fun row ->
                Some(row.PaymentCount.ToString(CultureInfo.InvariantCulture)))
            column "latestTransferDate" (fun row ->
                row.LatestTransferDate |> Option.map CalendarDate.format)
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
            column "changedAt" (fun row -> Some(Csv.instant row.ChangedAt))
            {
                Key = "basis.businessDate"
                Value =
                    fun basis _ _ ->
                        CsvValue.Text(CalendarDate.ofDate basis.BusinessDate |> CalendarDate.format)
            }
            {
                Key = "basis.timeZone"
                Value = fun basis _ _ -> CsvValue.Text basis.TimeZoneId
            }
        ]

    let private csv = Csv.row

    let csvHeader = columns |> List.map _.Key |> csv

    let csvChunks basis rows =
        rows
        |> Seq.collect (fun row ->
            let contributions =
                match row.Contributions with
                | [] -> [ None ]
                | values -> List.map Some values

            contributions
            |> Seq.map (fun contribution ->
                columns
                |> List.map (fun column -> column.Value basis row contribution)
                |> Csv.typedRow))

    let csvRows basis rows =
        csvChunks basis rows |> Seq.toArray |> Array.concat
