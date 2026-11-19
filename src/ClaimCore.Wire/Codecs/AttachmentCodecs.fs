namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module AttachmentCodecs =
    open Codec
    open ResponseScalars

    let id =
        uuid
        |> interpret
            (fun path raw ->
                AttachmentId.create raw
                |> Option.map Ok
                |> Option.defaultValue (
                    Error
                        {
                            Path = path
                            Reason = "Expected an attachment ID."
                        }
                ))
            AttachmentId.value

    let request =
        (record (fun target id ->
            {
                AttachmentReadRequest.Target = target
                AttachmentId = id
            })
        : Fields<AttachmentReadRequest, _>)
        |> field "target" _.Target RequestCodecs.caseSelector
        |> field "attachmentId" _.AttachmentId id
        |> build
        |> named "AttachmentReadRequest"

    let length = integer 0L (int64 AttachmentLimits.FileBytes)

    let name =
        boundedUtf8Text 1 AttachmentName.MaximumBytes
        |> interpret
            (fun path raw ->
                match AttachmentName.parse raw with
                | Some name when AttachmentName.value name = raw -> Ok name
                | _ ->
                    Error
                        {
                            Path = path
                            Reason = "Expected a canonical attachment name."
                        })
            AttachmentName.value

    let display =
        (record (fun name length ->
            {
                AttachmentDisplay.Name = name
                Length = length
            })
        : Fields<AttachmentDisplay, _>)
        |> field "name" _.Name name
        |> field "length" _.Length length
        |> build
        |> named "AttachmentDisplay"

    let sequence =
        integer 0L Sequence.Maximum
        |> map (Sequence.create >> Option.get) Sequence.value

    let row =
        (record (fun attachment name length recordedAt recordedBy canWithdraw previewCandidate ->
            {
                AttachmentRow.AttachmentId = attachment
                Name = name
                Length = length
                RecordedAt = recordedAt
                RecordedBy = recordedBy
                CanWithdraw = canWithdraw
                CanPreview = previewCandidate
            })
        : Fields<AttachmentRow, _>)
        |> field "attachmentId" _.AttachmentId id
        |> field "name" _.Name name
        |> field "length" _.Length length
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy ActorCodecs.actorRef
        |> field "canWithdraw" _.CanWithdraw boolean
        |> field "previewCandidate" _.CanPreview boolean
        |> build
        |> named "AttachmentRow"

    let page =
        (record (fun basis caseId reference disclosure items canAdd ->
            {
                AttachmentPage.Basis = basis
                CaseId = caseId
                Reference = reference
                Disclosure = disclosure
                Items = items
                CanAdd = canAdd
            })
        : Fields<AttachmentPage, _>)
        |> field "basis" _.Basis workDateBasis
        |> field "caseId" _.CaseId caseId
        |> field "reference" _.Reference caseReference
        |> field "disclosure" _.Disclosure sequence
        |> field "items" _.Items (list (int AttachmentLimits.RetainedFiles) row)
        |> field "canAdd" _.CanAdd boolean
        |> build
        |> named "AttachmentPage"
