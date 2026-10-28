namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

module internal TextOperationReply =
    open TextReplyFormat
    open TextDiagnosticReply
    open TextAuthorityReply

    let receiptLines language outcome (receipt: OperationReceipt) =
        let act, target, revision =
            match receipt.Subject with
            | ReceiptSubject.Case value ->
                term language CaseActKind.vocabulary value.Act,
                CaseReference.format value.Reference,
                [
                    label language (CliMessages.key CliMessage.RevisionLabel)
                    Revision.format value.Revision
                ]
            | ReceiptSubject.Authority value ->
                term language AuthorityKind.vocabulary value.Act,
                ActorId.format value.TargetActorId,
                []

        [
            String.concat
                " "
                ([ outcome; act; target ]
                 @ revision
                 @ [
                     label
                         language
                         (ContractDiscovery.inputLabel (
                             InteractionInput.Target InputTarget.OperationId
                         ))
                     OperationId.format receipt.OperationId
                 ])
        ]

    let requestInput request =
        request
        |> Option.bind (fun invocation ->
            RequestCodecs.operationInput.Read "$" (Frames.input invocation)
            |> Result.toOption)

    let reviewIdentity language request review =
        let input = requestInput request

        let act =
            input
            |> Option.map (function
                | OperationInput.Case value ->
                    term language CaseActKind.vocabulary (CaseAct.kind value.Act)
                | OperationInput.Authority value ->
                    term language AuthorityKind.vocabulary (AuthorityAct.kind value.Act))
            |> Option.defaultValue "—"

        let target =
            match review with
            | OperationReview.Case value -> CaseReference.format value.Proposed.Reference
            | OperationReview.Authority value ->
                let principal =
                    match input with
                    | Some(OperationInput.Authority value) ->
                        Some(value.Target.Kind, value.Target.Issuer, value.Target.Subject)
                    | _ -> value.Target |> Option.map (_.Principal >> PrincipalKey.parts)

                match principal with
                | Some(kind, issuer, subject) ->
                    String.concat " " [ principalKind language kind; issuer; subject ]
                | None -> "— — —"

        act + " " + target

    let reviewLines language request review =
        let changes =
            match review with
            | OperationReview.Authority value -> authorityChanges language value.Before value.After
            | OperationReview.Case value ->
                let before =
                    value.Before |> Option.bind (fun snapshot -> CaseState.fields snapshot.State)

                let after = CaseState.fields value.Proposed.State

                CaseFields.declarations
                |> List.choose (fun row ->
                    let previous =
                        before |> Option.bind (fun fields -> CaseFields.value row.Field fields)

                    let next =
                        after |> Option.bind (fun fields -> CaseFields.value row.Field fields)

                    if previous = next then
                        None
                    else
                        Some(
                            line
                                language
                                (FieldPresentation.labelId row.Field)
                                (displayed language previous + " → " + displayed language next)
                        ))

        line
            language
            (CliMessages.key CliMessage.ReviewTitle)
            (reviewIdentity language request review)
        :: changes

    let previewLines language request (reply: PreviewReply) =
        readLines
            language
            (function
            | Previewed.Reviewed review -> reviewLines language request review
            | Previewed.AlreadyAccepted receipt -> receiptLines language "ALREADY_ACCEPTED" receipt)
            reply.Outcome

    let executeLines language (reply: ExecuteReply) =
        let failed outcome =
            readLines language (fun () -> []) outcome

        match reply.Outcome with
        | ExecuteResult.Accepted receipt -> receiptLines language "ACCEPTED" receipt
        | ExecuteResult.AlreadyAccepted receipt -> receiptLines language "ALREADY_ACCEPTED" receipt
        | ExecuteResult.Rejected refusal -> failed (Outcome.Rejected refusal)
        | ExecuteResult.Failed fault -> failed (Outcome.Failed fault)
        | ExecuteResult.OutcomeUnknown ->
            [ label language (CliMessages.key CliMessage.UncertainDelivery) ]

    let resolveLines language (reply: Outcome<Resolved>) =
        readLines
            language
            (function
            | Resolved.Accepted receipt -> receiptLines language "ACCEPTED" receipt
            | Resolved.NotAccepted -> [ label language "notice.notAccepted" ])
            reply
