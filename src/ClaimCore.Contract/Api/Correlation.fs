namespace ClaimCore.Contract

open ClaimCore.Domain

open ClaimCore.Json

open System
open ClaimCore.Contract.Protocol

/// Typed request/reply consistency checks run after schema admission.
module Correlation =
    let private reference expected actual =
        match CaseReference.parse expected with
        | Ok reference -> CaseReference.identity reference = CaseReference.identity actual
        | Error _ -> false

    let private successor expected actual =
        expected >= 0L && expected < JsonValue.MaximumInteger && actual = expected + 1L

    let private act codec value token =
        codec.Write value |> JsonValue.tryMember "kind" = Some(JsonValue.String token)

    let private receipt input (receipt: OperationReceipt) =
        OperationInput.operationId input = receipt.OperationId
        && match input, receipt.Subject with
           | OperationInput.Case request, ReceiptSubject.Case accepted ->
               reference request.CaseReference accepted.Reference
               && successor request.ExpectedRevision accepted.Revision
               && act RequestCodecs.caseAct request.Act (CaseActKinds.token accepted.Act)
           | OperationInput.Authority request, ReceiptSubject.Authority accepted ->
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

    let execute input (reply: ExecuteReply) =
        OperationInput.operationId input = reply.OperationId
        && match reply.Outcome with
           | ExecuteResult.Accepted value
           | ExecuteResult.AlreadyAccepted value -> receipt input value
           | _ -> true

    let preview input (reply: PreviewReply) =
        OperationInput.operationId input = reply.OperationId
        && match reply.Outcome with
           | PreviewResult.AlreadyAccepted value -> receipt input value
           | PreviewResult.Reviewed value -> review input value
           | _ -> true

    let resolve (request: ResolveRequest) =
        function
        | ResolveReply.Accepted receipt -> request.OperationId = receipt.OperationId
        | _ -> true

    let observe (request: ObserveRequest) (reply: ReadReply<OperationReceipt>) =
        match reply with
        | ReadReply.Succeeded receipt -> request.OperationId = receipt.OperationId
        | _ -> true

    let grants (request: GrantsRequest) (reply: ReadReply<AuthorityGrantPage>) =
        match reply with
        | ReadReply.Succeeded page -> request.ActorId = page.Actor.ActorId
        | _ -> true

    let private admitted inputCodec outputCodec check input reply =
        match inputCodec.Read "$" input, outputCodec.Read "$" reply with
        | Ok input, Ok reply -> check input reply
        | _ -> false

    /// Raw wire consumers admit both values before using the same typed checks.
    let matches (endpoint: Route) input reply =
        match endpoint.Id with
        | EndpointId.Actor ActorEndpoint.Preview ->
            admitted Api.operationPreview.Input Api.operationPreview.Output preview input reply
        | EndpointId.Actor ActorEndpoint.Execute ->
            admitted Api.operationExecute.Input Api.operationExecute.Output execute input reply
        | EndpointId.Actor ActorEndpoint.Resolve ->
            admitted Api.operationResolve.Input Api.operationResolve.Output resolve input reply
        | EndpointId.Actor ActorEndpoint.Observe ->
            admitted Api.operationObserve.Input Api.operationObserve.Output observe input reply
        | EndpointId.Actor ActorEndpoint.AuthorityGrants ->
            admitted Api.authorityGrants.Input Api.authorityGrants.Output grants input reply
        | EndpointId.Actor(ActorEndpoint.Context | ActorEndpoint.CaseGet | ActorEndpoint.CaseList | ActorEndpoint.CaseHistory | ActorEndpoint.AuthorityList)
        | EndpointId.Host(HostEndpoint.Session | HostEndpoint.Logout) -> true
