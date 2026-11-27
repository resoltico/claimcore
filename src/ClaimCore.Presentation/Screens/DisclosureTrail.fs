namespace ClaimCore.Presentation

open System.Globalization
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module DisclosureTrailScreen =
    let private identifier format value =
        value |> format |> PresentedValue.Identifier

    let private exportField render value (item: RecordedDisclosure) =
        item.Event.Export |> Option.map value |> optional render

    let private selection (value: DisclosureExportSelection) =
        [
            $"filtered={string value.Filtered |> fun flag -> flag.ToLowerInvariant()}"
            $"paged={string value.Paged |> fun flag -> flag.ToLowerInvariant()}"
            yield! value.Order |> Option.map (CaseListOrder.token >> (+) "order=") |> Option.toList
            yield!
                value.Grouping
                |> Option.map (Vocabulary.token CaseGrouping.vocabulary >> (+) "grouping=")
                |> Option.toList
        ]
        |> String.concat "\n"
        |> PresentedValue.Narrative

    let private pageField render get (item: RecordedDisclosure) =
        item.Event.Page |> Option.map get |> optional render

    let private subjects format values =
        values |> List.map format |> String.concat "\n" |> PresentedValue.Narrative

    let private columns =
        [
            column
                "recordedAt"
                "label.recordedAt"
                Prominence.Primary
                false
                (fun (item: RecordedDisclosure) -> PresentedValue.Instant item.RecordedAt)
            column "actorId" "input.actorId.label" Prominence.Primary false (fun item ->
                identifier ActorId.format item.Event.ActorId)
            column "kind" "input.disclosureKind.label" Prominence.Primary false (fun item ->
                code DisclosureKind.vocabulary item.Event.Kind)
            column "caseId" "input.caseId.label" Prominence.Secondary false (fun item ->
                optional (identifier CaseId.format) item.Event.CaseId)
            column "attachmentId" "field.attachmentId.label" Prominence.Secondary false (fun item ->
                optional (identifier AttachmentId.format) item.Event.AttachmentId)
            column "outcome" "label.disclosureOutcome" Prominence.Secondary false (fun item ->
                code DisclosureOutcome.vocabulary item.Event.Outcome)
            column
                "rowCount"
                "label.disclosureRowCount"
                Prominence.Secondary
                false
                (pageField (int64 >> PresentedValue.Count) _.RowCount)
            column
                "cases"
                "label.disclosedCases"
                Prominence.Secondary
                false
                (pageField (subjects CaseId.format) _.Cases)
            column
                "people"
                "label.disclosedPeople"
                Prominence.Secondary
                false
                (pageField (subjects ActorId.format) _.People)
            column
                "exportQuery"
                "label.disclosureExportQuery"
                Prominence.Secondary
                false
                (exportField (DisclosureExportQuery.token >> PresentedValue.Identifier) _.Query)
            column
                "exportSelection"
                "label.disclosureExportSelection"
                Prominence.Secondary
                false
                (exportField selection _.Selection)
            column
                "exportRowCount"
                "label.disclosureExportRowCount"
                Prominence.Secondary
                false
                (exportField (int64 >> PresentedValue.Count) _.RowCount)
        ]

    let view (value: DisclosureTrailPage) =
        let rows =
            table
                (fun (item: RecordedDisclosure) -> item.Id.ToString(CultureInfo.InvariantCulture))
                (fun item ->
                    Some
                        {
                            Summary = None
                            Highlights = []
                            Sections = [ section "label.technicalDetails" (entries columns item) ]
                        })
                columns
                value.Items

        Screen.create
            [
                region RegionRole.Results None (RegionContent.Table rows)
                |> withEmpty "empty.disclosureTrail"
            ]

    let document (value: DisclosureTrailPage) =
        HumanDocument.listPage value.Basis (view value) value.NextCursor
