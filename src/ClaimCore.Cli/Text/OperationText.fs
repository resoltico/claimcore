namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Application.Contracts

/// Human operation text renders supplied core documents and messages, never DATA results.
module internal OperationText =
    let receiptLinesWithDetails details language alreadyRecorded (receipt: HumanDocument) =
        (if alreadyRecorded then
             [ ReplyTextFormat.label language "label.alreadyRecorded" ]
         else
             [])
        @ ViewText.renderDocument details language receipt

    let receiptLines language alreadyRecorded receipt =
        receiptLinesWithDetails false language alreadyRecorded receipt

    let private reviewLines details language (review: HumanReview) =
        let changes =
            review.Presentation.Changes
            |> List.filter (fun change ->
                match change.Before, change.After with
                | PresentedValue.Identifier _, _
                | _, PresentedValue.Identifier _ -> details
                | _ -> true)
            |> List.map (fun change ->
                ReplyTextFormat.line
                    language
                    change.LabelId
                    (TextDescriptorValue.render language change.Before
                     + " → "
                     + TextDescriptorValue.render language change.After))

        let unchanged =
            if review.Presentation.Unchanged.IsEmpty then
                "—"
            else
                review.Presentation.Unchanged
                |> List.map (fun entry -> ReplyTextFormat.label language entry.LabelId)
                |> List.distinct
                |> String.concat ", "

        changes
        @ [ ReplyTextFormat.line language "label.unchanged" unchanged ]
        @ ViewText.renderDocument details language review.Document
        @ (match review.Document.Basis with
           | ObservationBasis.Snapshot basis -> [ TextObservation.line language basis ]
           | ObservationBasis.Unavailable -> [])

    let previewLinesWithDetails
        details
        language
        _request
        (reply: RepresentationReply<HumanOperationReply<HumanPreviewResult>, PreviewReply>)
        =
        match reply with
        | RepresentationReply.Human value ->
            match value.Outcome with
            | HumanPreviewResult.Reviewed review -> reviewLines details language review
            | HumanPreviewResult.AlreadyAccepted receipt ->
                receiptLinesWithDetails details language true receipt
            | HumanPreviewResult.Rejected rejection ->
                HumanText.rejection details language rejection
            | HumanPreviewResult.Failed fault -> HumanText.fault language fault
        | RepresentationReply.Data _ -> invalidOp "Human text requires a HUMAN response."

    let previewLines language request reply =
        previewLinesWithDetails false language request reply

    let private mutationReceiptLines details language alreadyRecorded (receipt: HumanDocument) =
        let selected =
            match receipt.Context with
            | InteractionContext.Receipt {
                                             Target = ReceiptTarget.Case _
                                             RecordedFields = fields
                                         } ->
                fields |> List.map FieldPresentation.labelId |> Set.ofList
            | InteractionContext.Receipt { Target = ReceiptTarget.Work _ } ->
                Set.ofList [ "field.handlingSummary.label"; "field.responsibleActor.label" ]
            | _ ->
                receipt.Screen.Regions
                |> List.collect (fun region ->
                    match region.Content with
                    | RegionContent.Entries entries -> entries |> List.map _.LabelId
                    | _ -> [])
                |> Set.ofList

        let recordedFinancials =
            [ RecordGroup.Decision; RecordGroup.Payment ]
            |> List.collect BusinessField.ofGroup
            |> List.exists (fun field -> Set.contains (FieldPresentation.labelId field) selected)

        let screen =
            { receipt.Screen with
                Regions =
                    receipt.Screen.Regions
                    |> List.choose (fun region ->
                        match region.Role, region.Content with
                        | _ when
                            receipt.Disclosure = HumanDisclosure.Withheld
                            || receipt.Disclosure = HumanDisclosure.Erased
                            ->
                            Some region
                        | (RegionRole.Notice | RegionRole.Identity | RegionRole.Details), _ ->
                            Some region
                        | _, RegionContent.Entries entries ->
                            let entries =
                                entries
                                |> List.filter (fun entry ->
                                    Set.contains entry.LabelId selected
                                    || List.contains
                                        entry.LabelId
                                        [
                                            "field.claimantName.label"
                                            "field.insurerName.label"
                                            "field.claimedAmount.label"
                                        ])

                            if entries.IsEmpty then
                                None
                            else
                                Some
                                    { region with
                                        Content = RegionContent.Entries entries
                                    }
                        | RegionRole.Payments, _ when recordedFinancials -> Some region
                        | _ -> None)
            }

        receiptLinesWithDetails details language alreadyRecorded { receipt with Screen = screen }

    let executeLinesForWithDetails
        details
        language
        _request
        (reply: RepresentationReply<HumanOperationReply<HumanExecuteResult>, ExecuteReply>)
        =
        match reply with
        | RepresentationReply.Human value ->
            match value.Outcome with
            | HumanExecuteResult.Accepted receipt ->
                mutationReceiptLines details language false receipt
            | HumanExecuteResult.AlreadyAccepted receipt ->
                mutationReceiptLines details language true receipt
            | HumanExecuteResult.Rejected rejection ->
                HumanText.rejection details language rejection
            | HumanExecuteResult.Failed fault -> HumanText.fault language fault
            | HumanExecuteResult.OutcomeUnknown ->
                [
                    ReplyTextFormat.label language (CliMessages.key CliMessage.UncertainDelivery)
                ]
        | RepresentationReply.Data _ -> invalidOp "Human text requires a HUMAN response."

    let executeLinesFor language request reply =
        executeLinesForWithDetails false language request reply

    let executeLines language reply = executeLinesFor language None reply

    let getLinesWithDetails
        details
        language
        (reply:
            RepresentationReply<
                HumanOperationReply<HumanLookupResult>,
                OperationReply<OperationLookupResult>
             >)
        =
        match reply with
        | RepresentationReply.Human value ->
            match value.Outcome with
            | HumanLookupResult.Accepted(receipt, _) ->
                receiptLinesWithDetails details language false receipt
            | HumanLookupResult.NotAccepted document
            | HumanLookupResult.Pending(_, document) ->
                ViewText.renderDocument details language document
            | HumanLookupResult.Rejected rejection -> HumanText.rejection details language rejection
            | HumanLookupResult.Failed fault -> HumanText.fault language fault
        | RepresentationReply.Data _ -> invalidOp "Human text requires a HUMAN response."

    let getLines language reply =
        getLinesWithDetails false language reply
