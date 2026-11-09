namespace ClaimCore.Cli

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
        @ ViewText.renderSelected details language receipt.Screen

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
        @ ViewText.renderSelected details language review.Document.Screen
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
            | HumanPreviewResult.Failed advice -> HumanText.advice language advice
        | RepresentationReply.Data _ -> invalidOp "Human text requires a HUMAN response."

    let previewLines language request reply =
        previewLinesWithDetails false language request reply

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
                receiptLinesWithDetails details language false receipt
            | HumanExecuteResult.AlreadyAccepted receipt ->
                receiptLinesWithDetails details language true receipt
            | HumanExecuteResult.Rejected rejection ->
                HumanText.rejection details language rejection
            | HumanExecuteResult.Failed advice -> HumanText.advice language advice
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
                ViewText.renderSelected details language document.Screen
            | HumanLookupResult.Rejected rejection -> HumanText.rejection details language rejection
            | HumanLookupResult.Failed advice -> HumanText.advice language advice
        | RepresentationReply.Data _ -> invalidOp "Human text requires a HUMAN response."

    let getLines language reply =
        getLinesWithDetails false language reply
