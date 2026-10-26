namespace ClaimCore.Contract

open ClaimCore.Domain

open ClaimCore.Json

open System.Net.Http
open ClaimCore.Contract.Protocol

[<RequireQualifiedAccess>]
type EndpointEffect =
    | Read
    | OperationalWrite
    | LedgerExecution

module EndpointEffects =
    let vocabulary =
        Vocabulary.define
            "effect"
            [
                EndpointEffect.Read
                EndpointEffect.OperationalWrite
                EndpointEffect.LedgerExecution
            ]
            (function
             | EndpointEffect.Read -> "READ_ONLY"
             | EndpointEffect.OperationalWrite -> "OPERATIONAL_WRITE"
             | EndpointEffect.LedgerExecution -> "LEDGER_EXECUTION")

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type HostEndpoint =
    | Session
    | Logout

module HostEndpoints =
    let vocabulary =
        Vocabulary.define "endpoint" [ HostEndpoint.Session; HostEndpoint.Logout ] (function
            | HostEndpoint.Session -> "session"
            | HostEndpoint.Logout -> "session.logout")

    let all = Vocabulary.all vocabulary

[<RequireQualifiedAccess>]
type EndpointId =
    | Host of HostEndpoint
    | Actor of ActorEndpoint

module EndpointIds =
    let token =
        function
        | EndpointId.Host value -> Vocabulary.token HostEndpoints.vocabulary value
        | EndpointId.Actor value -> Vocabulary.token ActorEndpoints.vocabulary value

[<NoEquality; NoComparison>]
type Endpoint<'In, 'Out> =
    {
        Id: EndpointId
        Method: HttpMethod
        Effect: EndpointEffect
        Input: Codec<'In>
        Output: Codec<'Out>
    }

    member this.Token = EndpointIds.token this.Id

type IEndpointVisitor<'Result> =
    abstract Visit<'In, 'Out> : Endpoint<'In, 'Out> -> 'Result

type AnyEndpoint =
    abstract Accept<'Result> : IEndpointVisitor<'Result> -> 'Result

module Api =
    let private endpoint id method effect input output =
        {
            Id = id
            Method = method
            Effect = effect
            Input = input
            Output = output
        }

    let session =
        endpoint
            (EndpointId.Host HostEndpoint.Session)
            HttpMethod.Get
            EndpointEffect.Read
            RequestCodecs.empty
            (ReplyCodecs.readReply "SessionReply" ResponseCodecs.session)

    let logout =
        endpoint
            (EndpointId.Host HostEndpoint.Logout)
            HttpMethod.Post
            EndpointEffect.OperationalWrite
            RequestCodecs.empty
            (ReplyCodecs.readReply "LogoutReply" Codec.nullValue)

    let context =
        endpoint
            (EndpointId.Actor ActorEndpoint.Context)
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.empty
            (ReplyCodecs.readReply "ContextReply" ResponseCodecs.actorContext)

    let caseGet =
        endpoint
            (EndpointId.Actor ActorEndpoint.CaseGet)
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.caseGetRequest
            (ReplyCodecs.readReply "CaseReadReply" ResponseCodecs.caseView)

    let caseList =
        endpoint
            (EndpointId.Actor ActorEndpoint.CaseList)
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.pageRequest
            (ReplyCodecs.readReply "CasePageReply" ResponseCodecs.casesPage)

    let caseHistory =
        endpoint
            (EndpointId.Actor ActorEndpoint.CaseHistory)
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.historyRequest
            (ReplyCodecs.readReply
                "HistoryReply"
                (ResponseCodecs.historyPageFor ReceiptCodecs.caseReceipt))

    let operationPreview =
        endpoint
            (EndpointId.Actor ActorEndpoint.Preview)
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.operationInput
            ReplyCodecs.preview

    let operationExecute =
        endpoint
            (EndpointId.Actor ActorEndpoint.Execute)
            HttpMethod.Post
            EndpointEffect.LedgerExecution
            RequestCodecs.operationInput
            ReplyCodecs.execute

    let operationResolve =
        endpoint
            (EndpointId.Actor ActorEndpoint.Resolve)
            HttpMethod.Post
            EndpointEffect.OperationalWrite
            ResolutionCodecs.request
            ResolutionCodecs.reply

    let operationObserve =
        endpoint
            (EndpointId.Actor ActorEndpoint.Observe)
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.observeRequest
            (ReplyCodecs.readReply "ObservationReply" ReceiptCodecs.receipt)

    let authorityList =
        endpoint
            (EndpointId.Actor ActorEndpoint.AuthorityList)
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.pageRequest
            (ReplyCodecs.readReply "ActorsReply" ResponseCodecs.actorsPage)

    let authorityGrants =
        endpoint
            (EndpointId.Actor ActorEndpoint.AuthorityGrants)
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.grantsRequest
            (ReplyCodecs.readReply "GrantsReply" ResponseCodecs.grantsPage)

    let private boxed endpoint : AnyEndpoint =
        { new AnyEndpoint with
            member _.Accept visitor = visitor.Visit endpoint
        }

    let forHost =
        function
        | HostEndpoint.Session -> boxed session
        | HostEndpoint.Logout -> boxed logout

    let forActor =
        function
        | ActorEndpoint.Context -> boxed context
        | ActorEndpoint.CaseGet -> boxed caseGet
        | ActorEndpoint.CaseList -> boxed caseList
        | ActorEndpoint.CaseHistory -> boxed caseHistory
        | ActorEndpoint.Preview -> boxed operationPreview
        | ActorEndpoint.Execute -> boxed operationExecute
        | ActorEndpoint.Resolve -> boxed operationResolve
        | ActorEndpoint.Observe -> boxed operationObserve
        | ActorEndpoint.AuthorityList -> boxed authorityList
        | ActorEndpoint.AuthorityGrants -> boxed authorityGrants

    let all =
        (HostEndpoints.all |> List.map forHost)
        @ (ActorEndpoints.all |> List.map forActor)

    let identity (endpoint: AnyEndpoint) =
        endpoint.Accept
            { new IEndpointVisitor<EndpointId> with
                member _.Visit value = value.Id
            }

    let id (endpoint: AnyEndpoint) =
        endpoint.Accept
            { new IEndpointVisitor<string> with
                member _.Visit value = value.Token
            }

    let path endpoint = "/api/" + id endpoint

    let method (endpoint: AnyEndpoint) =
        endpoint.Accept
            { new IEndpointVisitor<HttpMethod> with
                member _.Visit value = value.Method
            }

    let effect (endpoint: AnyEndpoint) =
        endpoint.Accept
            { new IEndpointVisitor<EndpointEffect> with
                member _.Visit value = value.Effect
            }

    let executesLedger endpoint =
        effect endpoint = EndpointEffect.LedgerExecution

    let tryFind name =
        all |> List.tryFind (fun endpoint -> id endpoint = name)

    let admitInput (endpoint: AnyEndpoint) value =
        endpoint.Accept
            { new IEndpointVisitor<Result<unit, Problem>> with
                member _.Visit endpoint =
                    endpoint.Input.Read "$" value |> Result.map ignore
            }

    let admitOutput (endpoint: AnyEndpoint) value =
        endpoint.Accept
            { new IEndpointVisitor<Result<unit, Problem>> with
                member _.Visit endpoint =
                    endpoint.Output.Read "$" value |> Result.map ignore
            }
