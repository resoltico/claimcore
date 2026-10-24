namespace ClaimCore.Contract

open System
open ClaimCore.Contract.Protocol

/// Typed request/reply consistency checks run after schema admission.
module Correlation =
    let private reference (expected: string) (actual: string) =
        let valid (value: string) =
            value.Length >= 1
            && value.Length <= 80
            && value |> Seq.forall (fun character -> character >= '!' && character <= '~')

        valid expected
        && valid actual
        && String.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)

    let private successor expected actual =
        expected >= 0L && expected < JsonValue.MaximumInteger && actual = expected + 1L

    let private act codec value token =
        codec.Write value |> JsonValue.tryMember "kind" = Some(JsonValue.String token)

    let private receipt input (receipt: OperationReceipt) =
        OperationInput.operationId input = receipt.OperationId
        && match input, receipt.Scope with
           | OperationInput.Case request, ReceiptScope.Case accepted ->
               reference request.CaseReference accepted.Reference
               && successor request.ExpectedRevision accepted.Revision
               && act RequestCodecs.caseAct request.Act (CaseActKinds.token accepted.Act)
           | OperationInput.Authority request, ReceiptScope.Authority accepted ->
               act RequestCodecs.authorityAct request.Act (AuthorityKinds.token accepted.Act)
           | _ -> false

    let private review input =
        function
        | OperationReview.Case review ->
            match input with
            | OperationInput.Case request ->
                reference request.CaseReference review.Proposed.Reference
                && successor request.ExpectedRevision review.Proposed.Revision
            | _ -> false
        | OperationReview.Authority _ ->
            match input with
            | OperationInput.Authority _ -> true
            | _ -> false

    let private execute input (reply: ExecuteReply) =
        OperationInput.operationId input = reply.OperationId
        && match reply.Outcome with
           | ExecuteResult.Accepted value
           | ExecuteResult.AlreadyAccepted value -> receipt input value
           | _ -> true

    let private preview input (reply: PreviewReply) =
        OperationInput.operationId input = reply.OperationId
        && match reply.Outcome with
           | PreviewResult.AlreadyAccepted value -> receipt input value
           | PreviewResult.Reviewed value -> review input value
           | _ -> true

    let private admitted inputCodec outputCodec check input reply =
        match inputCodec.Read "$" input, outputCodec.Read "$" reply with
        | Ok input, Ok reply -> check input reply
        | _ -> false

    let matches endpoint input reply =
        match Api.id endpoint with
        | "operation.preview" ->
            admitted RequestCodecs.operationInput ReplyCodecs.preview preview input reply
        | "operation.execute" ->
            admitted RequestCodecs.operationInput ReplyCodecs.execute execute input reply
        | "operation.observe" ->
            admitted
                Api.operationObserve.Input
                Api.operationObserve.Output
                (fun request ->
                    function
                    | ReadReply.Succeeded receipt -> request.OperationId = receipt.OperationId
                    | _ -> true)
                input
                reply
        | "authority.grants" ->
            admitted
                Api.authorityGrants.Input
                Api.authorityGrants.Output
                (fun request ->
                    function
                    | ReadReply.Succeeded page -> request.ActorId = page.Actor.ActorId
                    | _ -> true)
                input
                reply
        | _ -> true
