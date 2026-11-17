namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Application.Contracts

/// Human operation text renders supplied core documents and messages, never DATA results.
module internal OperationText =
    let receiptLinesWithDetails
        details
        (rendering: RenderingContext)
        alreadyRecorded
        (receipt: HumanDocument)
        =
        (if alreadyRecorded then
             [ ReplyTextFormat.label rendering "label.alreadyRecorded" ]
         else
             [])
        @ ViewText.renderDocument details rendering receipt

    let receiptLines (rendering: RenderingContext) alreadyRecorded receipt =
        receiptLinesWithDetails false rendering alreadyRecorded receipt

    let private reviewLines details (rendering: RenderingContext) (review: HumanReview) =
        let changes =
            review.Presentation.Changes
            |> List.filter (fun change ->
                match change.Before, change.After with
                | PresentedValue.Identifier _, _
                | _, PresentedValue.Identifier _ -> details
                | _ -> true)
            |> List.map (fun change ->
                ReplyTextFormat.line
                    rendering
                    change.LabelId
                    (TextDescriptorValue.render rendering change.Before
                     + " → "
                     + TextDescriptorValue.render rendering change.After))

        let unchanged =
            if review.Presentation.Unchanged.IsEmpty then
                "—"
            else
                review.Presentation.Unchanged
                |> List.map (fun entry -> ReplyTextFormat.label rendering entry.LabelId)
                |> List.distinct
                |> String.concat ", "

        changes
        @ [ ReplyTextFormat.line rendering "label.unchanged" unchanged ]
        @ ViewText.renderDocument details rendering review.Document
        @ (match review.Document.Basis with
           | ObservationBasis.Snapshot basis -> [ TextObservation.line rendering basis ]
           | ObservationBasis.Unavailable -> [])

    let previewLinesWithDetails
        details
        rendering
        _request
        (reply: RepresentationReply<HumanOperationReply<HumanPreviewResult>, PreviewReply>)
        =
        match reply with
        | RepresentationReply.Human value ->
            match value.Outcome with
            | HumanPreviewResult.Reviewed review -> reviewLines details rendering review
            | HumanPreviewResult.AlreadyAccepted receipt ->
                receiptLinesWithDetails details rendering true receipt
            | HumanPreviewResult.Rejected rejection ->
                HumanText.rejection details rendering rejection
            | HumanPreviewResult.Failed fault -> HumanText.fault rendering fault
        | RepresentationReply.Data _ -> invalidOp "Human text requires a HUMAN response."

    let previewLines (rendering: RenderingContext) request reply =
        previewLinesWithDetails false rendering request reply

    let executeLinesForWithDetails
        details
        rendering
        _request
        (reply: RepresentationReply<HumanOperationReply<HumanExecuteResult>, ExecuteReply>)
        =
        match reply with
        | RepresentationReply.Human value ->
            match value.Outcome with
            | HumanExecuteResult.Accepted receipt ->
                receiptLinesWithDetails details rendering false receipt
            | HumanExecuteResult.AlreadyAccepted receipt ->
                receiptLinesWithDetails details rendering true receipt
            | HumanExecuteResult.Rejected rejection ->
                HumanText.rejection details rendering rejection
            | HumanExecuteResult.Failed fault -> HumanText.fault rendering fault
            | HumanExecuteResult.OutcomeUnknown ->
                [
                    ReplyTextFormat.label rendering (CliMessages.key CliMessage.UncertainDelivery)
                ]
        | RepresentationReply.Data _ -> invalidOp "Human text requires a HUMAN response."

    let executeLinesFor (rendering: RenderingContext) request reply =
        executeLinesForWithDetails false rendering request reply

    let executeLines (rendering: RenderingContext) reply = executeLinesFor rendering None reply

    let getLinesWithDetails
        details
        rendering
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
                receiptLinesWithDetails details rendering false receipt
            | HumanLookupResult.NotAccepted document
            | HumanLookupResult.Pending(_, document) ->
                ViewText.renderDocument details rendering document
            | HumanLookupResult.Rejected rejection ->
                HumanText.rejection details rendering rejection
            | HumanLookupResult.Failed fault -> HumanText.fault rendering fault
        | RepresentationReply.Data _ -> invalidOp "Human text requires a HUMAN response."

    let getLines (rendering: RenderingContext) reply =
        getLinesWithDetails false rendering reply
