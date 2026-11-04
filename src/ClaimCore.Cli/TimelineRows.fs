namespace ClaimCore.Cli

open System.Globalization
open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol.Presentation

/// A new timeline CSV projects the same typed descriptor values using canonical, unlocalized scalars.
module internal TimelineRows =
    let private canonical =
        function
        | PresentedValue.Money value -> Money.format value + " " + Money.currency value
        | PresentedValue.Date value -> CalendarDate.format value
        | PresentedValue.Instant value -> TextReplyFormat.instant value
        | PresentedValue.Party value -> PartyName.format value
        | PresentedValue.Text value
        | PresentedValue.Identifier value -> value
        | PresentedValue.Code(_, token) -> token
        | PresentedValue.Actor actor -> ActorId.format actor.ActorId
        | PresentedValue.State state -> CaseHeadline.token state
        | PresentedValue.Count count -> count.ToString(CultureInfo.InvariantCulture)
        | PresentedValue.Reference reference -> CaseReference.format reference
        | PresentedValue.Absent _ -> ""

    let private csv cells =
        (cells |> List.map CsvCell.format |> String.concat ",") + "\r\n"
        |> Encoding.UTF8.GetBytes

    let csvHeader =
        csv
            [
                "caseReference"
                "operationId"
                "sequence"
                "recordedAt"
                "recordedBy.actorId"
                "recordedBy.displayName.value"
                "recordedBy.displayName.kind"
                "act"
                "revision"
                "reminderId"
                "nextReminderId"
                "nextReminderRevision"
                "changes.kind"
                "field"
                "before"
                "after"
            ]

    let private rows reference (entry: CaseTimelineEntry) =
        let sections = Descriptor.timelineEntry entry

        let header =
            sections.Head.Entries
            |> List.map (fun entry -> entry.Key, canonical entry.Value)
            |> Map.ofList

        let read key =
            Map.tryFind key header |> Option.defaultValue ""

        let actorName, actorKind =
            match entry.RecordedBy.DisplayName with
            | DisplayName.Named value -> PartyName.format value, "NAMED"
            | DisplayName.Erased -> "", "ERASED"

        let disclosure =
            match entry.Changes with
            | TimelineChanges.Disclosed _ -> "DISCLOSED"
            | TimelineChanges.Withheld -> "WITHHELD"
            | TimelineChanges.Erased -> "ERASED"

        let metadata =
            [
                CaseReference.format reference
                read "operationId"
                Sequence.format entry.Position.Sequence
                read "recordedAt"
                ActorId.format entry.RecordedBy.ActorId
                actorName
                actorKind
                read "act"
                read "revision"
                read "reminderId"
                read "nextReminderId"
                read "nextReminderRevision"
                disclosure
            ]

        let differences =
            sections.Tail
            |> List.choose (fun section ->
                match section.Entries with
                | [ before; after ] when
                    before.Key.EndsWith(".before", System.StringComparison.Ordinal)
                    ->
                    Some
                        [
                            before.Key.Substring(0, before.Key.Length - ".before".Length)
                            canonical before.Value
                            canonical after.Value
                        ]
                | _ -> None)

        let differences =
            if differences.IsEmpty then
                [ [ ""; ""; "" ] ]
            else
                differences

        differences |> List.map (fun values -> csv (metadata @ values))

    let csvRows (page: CaseTimelinePage) =
        page.Items |> List.collect (rows page.Reference) |> Array.concat
