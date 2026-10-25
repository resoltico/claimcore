namespace ClaimCore.Contract

open System.Net.Http
open ClaimCore.Contract.Protocol

[<RequireQualifiedAccess>]
type EndpointEffect =
    | Read
    | OperationalWrite
    | LedgerExecution

module EndpointEffects =
    let token =
        function
        | EndpointEffect.Read -> "READ_ONLY"
        | EndpointEffect.OperationalWrite -> "OPERATIONAL_WRITE"
        | EndpointEffect.LedgerExecution -> "LEDGER_EXECUTION"

[<NoEquality; NoComparison>]
type Endpoint<'In, 'Out> =
    {
        Id: string
        Method: HttpMethod
        Effect: EndpointEffect
        Input: Codec<'In>
        Output: Codec<'Out>
    }

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
            "session"
            HttpMethod.Get
            EndpointEffect.Read
            RequestCodecs.empty
            (ReplyCodecs.readReply "SessionReply" ResponseCodecs.session)

    let logout =
        { endpoint
              "session.logout"
              HttpMethod.Post
              EndpointEffect.Read
              RequestCodecs.empty
              (ReplyCodecs.readReply "LogoutReply" Codec.nullValue) with
            Effect = EndpointEffect.OperationalWrite
        }

    let context =
        endpoint
            "context"
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.empty
            (ReplyCodecs.readReply "ContextReply" ResponseCodecs.actorContext)

    let caseGet =
        endpoint
            "case.get"
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.caseGetRequest
            (ReplyCodecs.readReply "CaseReadReply" ResponseCodecs.caseView)

    let caseList =
        endpoint
            "case.list"
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.pageRequest
            (ReplyCodecs.readReply "CasePageReply" ResponseCodecs.casesPage)

    let caseHistory =
        endpoint
            "case.history"
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.historyRequest
            (ReplyCodecs.readReply
                "HistoryReply"
                (ResponseCodecs.historyPageFor ReceiptCodecs.caseReceipt))

    let operationPreview =
        endpoint
            "operation.preview"
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.operationInput
            ReplyCodecs.preview

    let operationExecute =
        endpoint
            "operation.execute"
            HttpMethod.Post
            EndpointEffect.LedgerExecution
            RequestCodecs.operationInput
            ReplyCodecs.execute

    let operationResolve =
        endpoint
            "operation.resolve"
            HttpMethod.Post
            EndpointEffect.OperationalWrite
            ResolutionCodecs.request
            ResolutionCodecs.reply

    let operationObserve =
        endpoint
            "operation.observe"
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.observeRequest
            (ReplyCodecs.readReply "ObservationReply" ReceiptCodecs.receipt)

    let authorityList =
        endpoint
            "authority.list"
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.pageRequest
            (ReplyCodecs.readReply "ActorsReply" ResponseCodecs.actorsPage)

    let authorityGrants =
        endpoint
            "authority.grants"
            HttpMethod.Post
            EndpointEffect.Read
            RequestCodecs.grantsRequest
            (ReplyCodecs.readReply "GrantsReply" ResponseCodecs.grantsPage)

    let private boxed endpoint : AnyEndpoint =
        { new AnyEndpoint with
            member _.Accept visitor = visitor.Visit endpoint
        }

    let all =
        [
            boxed session
            boxed logout
            boxed context
            boxed caseGet
            boxed caseList
            boxed caseHistory
            boxed operationPreview
            boxed operationExecute
            boxed operationResolve
            boxed operationObserve
            boxed authorityList
            boxed authorityGrants
        ]

    let id (endpoint: AnyEndpoint) =
        endpoint.Accept
            { new IEndpointVisitor<string> with
                member _.Visit value = value.Id
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
