namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module internal TextOperationReply =
    open ClaimCore.Protocol.Presentation

    open TextReplyFormat
    open TextDiagnosticReply
    open TextAuthorityReply

    let receiptLines language alreadyRecorded (receipt: OperationReceipt) =
        [
            label
                language
                (if alreadyRecorded then
                     "label.alreadyRecorded"
                 else
                     "label.recorded")
        ]
        @ TextDescriptorValue.sections language (Descriptor.receipt receipt)
        @ [ TextObservation.line language receipt.Basis ]

    let requestInput request =
        request
        |> Option.bind (fun invocation ->
            Frames.value Api.operationExecute invocation
            |> Option.orElseWith (fun () -> Frames.value Api.operationPreview invocation))

    let private reviewIdentity language input review =

        let act =
            input
            |> Option.map (function
                | OperationInput.Case value ->
                    term language CaseActKind.vocabulary (CaseAct.kind value.Act)
                | OperationInput.Work _ ->
                    term language WorkActKind.vocabulary WorkActKind.UpdateWork
                | OperationInput.Reminder value ->
                    term language ReminderActKind.vocabulary (AuthoredReminderAct.kind value.Act)
                | OperationInput.Authority value ->
                    term language AuthorityKind.vocabulary (AuthorityAct.kind value.Act))
            |> Option.defaultValue "—"

        let target =
            match review with
            | OperationReview.Case value ->
                TerminalText.data (CaseReference.format value.Proposed.Reference)
            | OperationReview.Work value -> TerminalText.data (CaseReference.format value.Reference)
            | OperationReview.Reminder value ->
                TerminalText.data (CaseReference.format value.Reference)
            | OperationReview.Authority value ->
                let principal =
                    match input with
                    | Some(OperationInput.Authority value) ->
                        Some(value.Target.Kind, value.Target.Issuer, value.Target.Subject)
                    | _ -> value.Target |> Option.map (_.Principal >> PrincipalKey.parts)

                match principal with
                | Some(kind, issuer, subject) ->
                    String.concat
                        " "
                        [
                            principalKind language kind
                            TerminalText.data issuer
                            displayed language (Some subject)
                        ]
                | None -> "— — —"

        act + " " + target

    let reviewLines language request review =
        let description = Descriptor.operationReview (requestInput request) review

        [
            line
                language
                (CliMessages.key CliMessage.ReviewTitle)
                (reviewIdentity language (requestInput request) review)
        ]
        @ (description.Changes
           |> List.map (fun change ->
               line
                   language
                   change.LabelId
                   (TextDescriptorValue.render language change.Before
                    + " → "
                    + TextDescriptorValue.render language change.After)))
        @ [
            line
                language
                "label.unchanged"
                (if description.Unchanged.IsEmpty then
                     "—"
                 else
                     description.Unchanged
                     |> List.map (fun entry -> label language entry.LabelId)
                     |> List.distinct
                     |> String.concat ", ")
        ]
        @ TextDescriptorValue.sections language description.Effects
        @ [ TextObservation.line language description.Basis ]

    let previewLines language request (reply: PreviewReply) =
        readLinesFor
            language
            (TextDiagnosticReply.reference request)
            (function
            | Previewed.Reviewed review -> reviewLines language request review
            | Previewed.AlreadyAccepted receipt -> receiptLines language true receipt)
            reply.Outcome

    let executeLinesFor language request (reply: ExecuteReply) =
        let failed outcome =
            readLinesFor language (TextDiagnosticReply.reference request) (fun () -> []) outcome

        match reply.Outcome with
        | ExecuteResult.Accepted receipt -> receiptLines language false receipt
        | ExecuteResult.AlreadyAccepted receipt -> receiptLines language true receipt
        | ExecuteResult.Rejected refusal -> failed (Outcome.Rejected refusal)
        | ExecuteResult.Failed fault -> failed (Outcome.Failed fault)
        | ExecuteResult.OutcomeUnknown ->
            [ label language (CliMessages.key CliMessage.UncertainDelivery) ]

    let executeLines language reply = executeLinesFor language None reply

    let resolveLines language (reply: Outcome<Resolved>) =
        readLines
            language
            (function
            | Resolved.Accepted receipt -> receiptLines language false receipt
            | Resolved.NotAccepted -> [ label language "notice.notAccepted" ])
            reply

    let statusLines language (reply: Outcome<OperationStatus>) =
        readLines
            language
            (function
            | OperationStatus.Accepted receipt -> receiptLines language false receipt
            | OperationStatus.NotAccepted -> [ label language "notice.notAccepted" ]
            | OperationStatus.Unresolved ->
                [ MessageCatalogs.renderCli language CliMessage.StatusUnresolved ])
            reply
