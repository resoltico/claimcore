namespace ClaimCore.Cli

open ClaimCore.Contract.Protocol

open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Json

/// A decoded reply retains its original admitted wire value for JSON delivery.
[<RequireQualifiedAccess; NoComparison>]
type ServiceReply =
    | Session of JsonValue * ReadReply<Session>
    | Logout of JsonValue * ReadReply<unit>
    | Preview of JsonValue * PreviewReply
    | Execute of JsonValue * ExecuteReply
    | CaseGet of JsonValue * ReadReply<CaseReadModel>
    | CaseList of JsonValue * ReadReply<CaseSummaryPage>
    | CaseHistory of JsonValue * ReadReply<HistoryResultPage>
    | Observe of JsonValue * ReadReply<OperationReceipt>
    | Resolve of JsonValue * ResolveReply
    | AuthorityList of JsonValue * ReadReply<ActorReviewPage>
    | AuthorityGrants of JsonValue * ReadReply<AuthorityGrantPage>
    | Context of JsonValue * ReadReply<ActorContext>

    member this.Endpoint =
        match this with
        | ServiceReply.Session _ -> EndpointId.Host HostEndpoint.Session
        | ServiceReply.Logout _ -> EndpointId.Host HostEndpoint.Logout
        | ServiceReply.Preview _ -> EndpointId.Actor ActorEndpoint.Preview
        | ServiceReply.Execute _ -> EndpointId.Actor ActorEndpoint.Execute
        | ServiceReply.CaseGet _ -> EndpointId.Actor ActorEndpoint.CaseGet
        | ServiceReply.CaseList _ -> EndpointId.Actor ActorEndpoint.CaseList
        | ServiceReply.CaseHistory _ -> EndpointId.Actor ActorEndpoint.CaseHistory
        | ServiceReply.Observe _ -> EndpointId.Actor ActorEndpoint.Observe
        | ServiceReply.Resolve _ -> EndpointId.Actor ActorEndpoint.Resolve
        | ServiceReply.AuthorityList _ -> EndpointId.Actor ActorEndpoint.AuthorityList
        | ServiceReply.AuthorityGrants _ -> EndpointId.Actor ActorEndpoint.AuthorityGrants
        | ServiceReply.Context _ -> EndpointId.Actor ActorEndpoint.Context

module ServiceReply =
    let json =
        function
        | ServiceReply.Session(wire, _)
        | ServiceReply.Logout(wire, _)
        | ServiceReply.Preview(wire, _)
        | ServiceReply.Execute(wire, _)
        | ServiceReply.CaseGet(wire, _)
        | ServiceReply.CaseList(wire, _)
        | ServiceReply.CaseHistory(wire, _)
        | ServiceReply.Observe(wire, _)
        | ServiceReply.Resolve(wire, _)
        | ServiceReply.AuthorityList(wire, _)
        | ServiceReply.AuthorityGrants(wire, _)
        | ServiceReply.Context(wire, _) -> wire

    let private decode value (codec: Codec<'Reply>) make =
        codec.Read "$" value |> Result.map (fun reply -> make (value, reply))

    let private actor actor value =
        match actor with
        | ActorEndpoint.Preview -> decode value Api.operationPreview.Output ServiceReply.Preview
        | ActorEndpoint.Execute -> decode value Api.operationExecute.Output ServiceReply.Execute
        | ActorEndpoint.CaseGet -> decode value Api.caseGet.Output ServiceReply.CaseGet
        | ActorEndpoint.CaseList -> decode value Api.caseList.Output ServiceReply.CaseList
        | ActorEndpoint.CaseHistory -> decode value Api.caseHistory.Output ServiceReply.CaseHistory
        | ActorEndpoint.Observe -> decode value Api.operationObserve.Output ServiceReply.Observe
        | ActorEndpoint.Resolve -> decode value Api.operationResolve.Output ServiceReply.Resolve
        | ActorEndpoint.AuthorityList ->
            decode value Api.authorityList.Output ServiceReply.AuthorityList
        | ActorEndpoint.AuthorityGrants ->
            decode value Api.authorityGrants.Output ServiceReply.AuthorityGrants
        | ActorEndpoint.Context -> decode value Api.context.Output ServiceReply.Context

    let read (endpoint: ClaimCore.Contract.Protocol.Route) value =
        match endpoint.Id with
        | EndpointId.Host HostEndpoint.Session ->
            decode value Api.session.Output ServiceReply.Session
        | EndpointId.Host HostEndpoint.Logout -> decode value Api.logout.Output ServiceReply.Logout
        | EndpointId.Actor endpoint -> actor endpoint value

    let private check input (codec: Codec<'Request>) correlate reply =
        codec.Read "$" input |> Result.exists (fun request -> correlate request reply)

    /// Reply admission already established its shape; correlation keeps that decoded value.
    let correlates input =
        function
        | ServiceReply.Preview(_, reply) ->
            check input Api.operationPreview.Input Correlation.preview reply
        | ServiceReply.Execute(_, reply) ->
            check input Api.operationExecute.Input Correlation.execute reply
        | ServiceReply.Resolve(_, reply) ->
            check input Api.operationResolve.Input Correlation.resolve reply
        | ServiceReply.Observe(_, reply) ->
            check input Api.operationObserve.Input Correlation.observe reply
        | ServiceReply.AuthorityGrants(_, reply) ->
            check input Api.authorityGrants.Input Correlation.grants reply
        | ServiceReply.Session _
        | ServiceReply.Logout _
        | ServiceReply.CaseGet _
        | ServiceReply.CaseList _
        | ServiceReply.CaseHistory _
        | ServiceReply.AuthorityList _
        | ServiceReply.Context _ -> true
