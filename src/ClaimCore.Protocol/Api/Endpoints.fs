namespace ClaimCore.Protocol

open ClaimCore.Protocol

open ClaimCore.Domain

open ClaimCore.Json

open System.Net.Http

[<RequireQualifiedAccess>]
type EndpointEffect =
    | Read
    | OperationalWrite
    | LedgerExecution

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module EndpointEffect =
    let vocabulary =
        Vocabulary.ofUnion<EndpointEffect> "effect" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type HostEndpoint =
    | Session
    | Logout

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module HostEndpoint =
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
        | EndpointId.Host value -> Vocabulary.token HostEndpoint.vocabulary value
        | EndpointId.Actor value -> Vocabulary.token ActorEndpoint.vocabulary value

[<NoEquality; NoComparison>]
type Endpoint<'In, 'Out> =
    {
        Id: EndpointId
        Method: HttpMethod
        Effect: EndpointEffect
        Input: Codec<'In>
        Output: Codec<'Out>
        Classify: 'Out -> ReplyClass
        Correlation: 'In -> System.Guid option
        Unavailable: 'In -> 'Out
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
        ClassifyOutput: JsonValue -> Result<ReplyClass, Problem>
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
            ClassifyOutput =
                fun value -> endpoint.Output.Read "$" value |> Result.map endpoint.Classify
            NormalizeOutput =
                fun value -> endpoint.Output.Read "$" value |> Result.map endpoint.Output.Write
        }

module Endpoint =
    let reply (endpoint: Endpoint<'Input, 'Output>) value : ClassifiedReply =
        {
            Wire = endpoint.Output.Write value
            Class = endpoint.Classify value
        }

module Api =
    let private read id method input output =
        {
            Id = id
            Method = method
            Effect = EndpointEffect.Read
            Input = input
            Output = output
            Classify = OutcomePresentation.classify
            Correlation = fun _ -> None
            Unavailable = fun _ -> Outcome.Failed CoreFault.StoreUnavailable
        }

    let session =
        read
            (EndpointId.Host HostEndpoint.Session)
            HttpMethod.Get
            RequestCodecs.empty
            (ReplyCodecs.readReply "SessionReply" ResponseCodecs.session)

    let logout =
        { read
              (EndpointId.Host HostEndpoint.Logout)
              HttpMethod.Post
              RequestCodecs.empty
              (ReplyCodecs.readReply "LogoutReply" Codec.nullValue) with
            Effect = EndpointEffect.OperationalWrite
        }

    let context =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Context))
            HttpMethod.Post
            RequestCodecs.empty
            (ReplyCodecs.readReply "ContextReply" ResponseCodecs.actorContext)

    let caseGet =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseGet))
            HttpMethod.Post
            RequestCodecs.caseGetRequest
            (ReplyCodecs.readReply "CaseReadReply" ResponseCodecs.caseView)

    let caseList =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseList))
            HttpMethod.Post
            RequestCodecs.caseListRequest
            (ReplyCodecs.readReply "CasePageReply" ResponseCodecs.casesPage)

    let caseHistory =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseHistory))
            HttpMethod.Post
            RequestCodecs.historyRequest
            (ReplyCodecs.readReply
                "HistoryReply"
                (ResponseCodecs.historyPageFor ReceiptCodecs.caseReceipt))

    let operationPreview =
        {
            Id = EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview)
            Method = HttpMethod.Post
            Effect = EndpointEffect.Read
            Input = RequestCodecs.operationInput
            Output = ReplyCodecs.preview
            Classify = OutcomePresentation.classifyPreview
            Correlation = OperationInput.operationId >> Some
            Unavailable =
                fun input ->
                    {
                        PreviewReply.OperationId = OperationInput.operationId input
                        Outcome = Outcome.Failed CoreFault.StoreUnavailable
                    }
        }

    let operationExecute =
        {
            Id = EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute)
            Method = HttpMethod.Post
            Effect = EndpointEffect.LedgerExecution
            Input = RequestCodecs.operationInput
            Output = ReplyCodecs.execute
            Classify = fun reply -> OutcomePresentation.classifyExecute reply.Outcome
            Correlation = OperationInput.operationId >> Some
            Unavailable =
                fun input ->
                    {
                        ExecuteReply.OperationId = OperationInput.operationId input
                        Outcome = ExecuteResult.Failed CoreFault.StoreUnavailable
                    }
        }

    let operationResolve =
        { read
              (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Resolve))
              HttpMethod.Post
              ResolutionCodecs.request
              ResolutionCodecs.reply with
            Effect = EndpointEffect.OperationalWrite
            Classify = OutcomePresentation.classifyResolve
            Correlation = fun input -> Some input.OperationId
        }

    let operationObserve =
        { read
              (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Observe))
              HttpMethod.Post
              RequestCodecs.observeRequest
              (ReplyCodecs.readReply "ObservationReply" ReceiptCodecs.receipt) with
            Correlation = fun input -> Some input.OperationId
        }

    let authorityList =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityList))
            HttpMethod.Post
            RequestCodecs.pageRequest
            (ReplyCodecs.readReply "ActorsReply" ResponseCodecs.actorsPage)

    let authorityGrants =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityGrants))
            HttpMethod.Post
            RequestCodecs.grantsRequest
            (ReplyCodecs.readReply "GrantsReply" ResponseCodecs.grantsPage)

    let forHost =
        function
        | HostEndpoint.Session -> Route.describe session
        | HostEndpoint.Logout -> Route.describe logout

    let forActor =
        function
        | (ActorEndpoint.Query QueryEndpoint.Context) -> Route.describe context
        | (ActorEndpoint.Query QueryEndpoint.CaseGet) -> Route.describe caseGet
        | (ActorEndpoint.Query QueryEndpoint.CaseList) -> Route.describe caseList
        | (ActorEndpoint.Query QueryEndpoint.CaseHistory) -> Route.describe caseHistory
        | (ActorEndpoint.Operation OperationEndpoint.Preview) -> Route.describe operationPreview
        | (ActorEndpoint.Operation OperationEndpoint.Execute) -> Route.describe operationExecute
        | (ActorEndpoint.Query QueryEndpoint.Resolve) -> Route.describe operationResolve
        | (ActorEndpoint.Query QueryEndpoint.Observe) -> Route.describe operationObserve
        | (ActorEndpoint.Query QueryEndpoint.AuthorityList) -> Route.describe authorityList
        | (ActorEndpoint.Query QueryEndpoint.AuthorityGrants) -> Route.describe authorityGrants

    let all =
        (HostEndpoint.all |> List.map forHost)
        @ (ActorEndpoint.all |> List.map forActor)

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
