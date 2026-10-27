namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

open ClaimCore.Domain

open ClaimCore.Json

open System.Net.Http

[<RequireQualifiedAccess>]
type EndpointEffect =
    | Read
    | OperationalWrite
    | LedgerExecution

module EndpointEffects =
    let vocabulary =
        Vocabulary.ofUnion<EndpointEffect> "effect" TokenCasing.ScreamingSnake None

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

[<NoEquality; NoComparison>]
type Route =
    {
        Id: EndpointId
        Method: HttpMethod
        Effect: EndpointEffect
        InputSchema: Schema
        OutputSchema: Schema
        InputDefinitions: Map<string, Schema>
        OutputDefinitions: Map<string, Schema>
        NormalizeInput: JsonValue -> Result<JsonValue, Problem>
        NormalizeOutput: JsonValue -> Result<JsonValue, Problem>
    }

    member this.Token = EndpointIds.token this.Id

module Route =
    let describe (endpoint: Endpoint<'Input, 'Output>) =
        {
            Id = endpoint.Id
            Method = endpoint.Method
            Effect = endpoint.Effect
            InputSchema = endpoint.Input.Schema
            OutputSchema = endpoint.Output.Schema
            InputDefinitions = endpoint.Input.Definitions
            OutputDefinitions = endpoint.Output.Definitions
            NormalizeInput =
                fun value -> endpoint.Input.Read "$" value |> Result.map endpoint.Input.Write
            NormalizeOutput =
                fun value -> endpoint.Output.Read "$" value |> Result.map endpoint.Output.Write
        }

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


    let forHost =
        function
        | HostEndpoint.Session -> Route.describe session
        | HostEndpoint.Logout -> Route.describe logout

    let forActor =
        function
        | ActorEndpoint.Context -> Route.describe context
        | ActorEndpoint.CaseGet -> Route.describe caseGet
        | ActorEndpoint.CaseList -> Route.describe caseList
        | ActorEndpoint.CaseHistory -> Route.describe caseHistory
        | ActorEndpoint.Preview -> Route.describe operationPreview
        | ActorEndpoint.Execute -> Route.describe operationExecute
        | ActorEndpoint.Resolve -> Route.describe operationResolve
        | ActorEndpoint.Observe -> Route.describe operationObserve
        | ActorEndpoint.AuthorityList -> Route.describe authorityList
        | ActorEndpoint.AuthorityGrants -> Route.describe authorityGrants

    let all =
        (HostEndpoints.all |> List.map forHost)
        @ (ActorEndpoints.all |> List.map forActor)

    let identity (endpoint: Route) = endpoint.Id
    let id (endpoint: Route) = endpoint.Token
    let path endpoint = "/api/" + id endpoint
    let method (endpoint: Route) = endpoint.Method
    let effect (endpoint: Route) = endpoint.Effect

    let executesLedger endpoint =
        effect endpoint = EndpointEffect.LedgerExecution

    let tryFind name =
        all |> List.tryFind (fun endpoint -> id endpoint = name)

    let admitInput (endpoint: Route) value =
        endpoint.NormalizeInput value |> Result.map ignore

    let admitOutput (endpoint: Route) value =
        endpoint.NormalizeOutput value |> Result.map ignore
