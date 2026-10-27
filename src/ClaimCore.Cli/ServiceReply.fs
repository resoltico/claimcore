namespace ClaimCore.Cli

open ClaimCore.Protocol

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
        | ServiceReply.Preview _ ->
            EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview)
        | ServiceReply.Execute _ ->
            EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute)
        | ServiceReply.CaseGet _ -> EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseGet)
        | ServiceReply.CaseList _ -> EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseList)
        | ServiceReply.CaseHistory _ ->
            EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseHistory)
        | ServiceReply.Observe _ -> EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Observe)
        | ServiceReply.Resolve _ -> EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Resolve)
        | ServiceReply.AuthorityList _ ->
            EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityList)
        | ServiceReply.AuthorityGrants _ ->
            EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityGrants)
        | ServiceReply.Context _ -> EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Context)

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
        | (ActorEndpoint.Operation OperationEndpoint.Preview) ->
            decode value Api.operationPreview.Output ServiceReply.Preview
        | (ActorEndpoint.Operation OperationEndpoint.Execute) ->
            decode value Api.operationExecute.Output ServiceReply.Execute
        | (ActorEndpoint.Query QueryEndpoint.CaseGet) ->
            decode value Api.caseGet.Output ServiceReply.CaseGet
        | (ActorEndpoint.Query QueryEndpoint.CaseList) ->
            decode value Api.caseList.Output ServiceReply.CaseList
        | (ActorEndpoint.Query QueryEndpoint.CaseHistory) ->
            decode value Api.caseHistory.Output ServiceReply.CaseHistory
        | (ActorEndpoint.Query QueryEndpoint.Observe) ->
            decode value Api.operationObserve.Output ServiceReply.Observe
        | (ActorEndpoint.Query QueryEndpoint.Resolve) ->
            decode value Api.operationResolve.Output ServiceReply.Resolve
        | (ActorEndpoint.Query QueryEndpoint.AuthorityList) ->
            decode value Api.authorityList.Output ServiceReply.AuthorityList
        | (ActorEndpoint.Query QueryEndpoint.AuthorityGrants) ->
            decode value Api.authorityGrants.Output ServiceReply.AuthorityGrants
        | (ActorEndpoint.Query QueryEndpoint.Context) ->
            decode value Api.context.Output ServiceReply.Context

    let read (endpoint: ClaimCore.Protocol.Route) value =
        match endpoint.Id with
        | EndpointId.Host HostEndpoint.Session ->
            decode value Api.session.Output ServiceReply.Session
        | EndpointId.Host HostEndpoint.Logout -> decode value Api.logout.Output ServiceReply.Logout
        | EndpointId.Actor endpoint -> actor endpoint value
