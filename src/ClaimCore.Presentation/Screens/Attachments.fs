namespace ClaimCore.Presentation

open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ScreenEntries

module AttachmentsScreen =
    let details (display: AttachmentDisplay) =
        [
            value
                "attachmentName"
                "field.attachmentName.label"
                Prominence.Primary
                (PresentedValue.Narrative(AttachmentName.value display.Name))
            value
                "attachmentLength"
                "field.attachmentLength.label"
                Prominence.Secondary
                (PresentedValue.Count display.Length)
        ]

    let page (page: AttachmentPage) =
        let columns =
            [
                column
                    "attachmentName"
                    "field.attachmentName.label"
                    Prominence.Primary
                    false
                    (fun (item: AttachmentRow) ->
                        PresentedValue.Narrative(AttachmentName.value item.Name))
                column
                    "attachmentLength"
                    "field.attachmentLength.label"
                    Prominence.Secondary
                    false
                    (fun item -> PresentedValue.Count item.Length)
                column "recordedBy" "label.recordedBy" Prominence.Secondary false (fun item ->
                    PresentedValue.Actor item.RecordedBy)
                column "recordedAt" "label.recordedAt" Prominence.Secondary false (fun item ->
                    PresentedValue.Instant item.RecordedAt)
            ]

        let table =
            tableWithTransfers
                (fun (item: AttachmentRow) -> AttachmentId.format item.AttachmentId)
                (fun item ->
                    Some
                        {
                            Summary = None
                            Highlights = []
                            Sections =
                                [
                                    section
                                        "label.technicalDetails"
                                        [
                                            value
                                                "attachmentId"
                                                "field.attachmentId.label"
                                                Prominence.Detail
                                                (PresentedValue.Identifier(
                                                    AttachmentId.format item.AttachmentId
                                                ))
                                        ]
                                ]
                        })
                columns
                (fun item -> None, TaskAffordances.attachmentWithdraw page.CaseId item)
                (fun item ->
                    AttachmentRead.offered item.CanPreview
                    |> List.map (fun transfer ->
                        {
                            Kind = transfer
                            LabelId = (EndpointDeclarations.attachmentRead transfer).LabelId
                            Request =
                                {
                                    Target = CaseSelector.Id page.CaseId
                                    AttachmentId = item.AttachmentId
                                }
                            Name = item.Name
                        }))
                page.Items

        Screen.create
            [
                entryRegion
                    RegionRole.Identity
                    None
                    [
                        value
                            "caseReference"
                            "field.caseReference.label"
                            Prominence.Primary
                            (PresentedValue.Reference page.Reference)
                    ]
                region
                    RegionRole.Attachments
                    (Some "section.attachments.label")
                    (RegionContent.Table table)
                |> withEmpty "empty.attachments"
                |> withCount "attachments" (int64 page.Items.Length)
            ]
