namespace ClaimCore.Contract

open System.Net.Http
open ClaimCore.Contract.Protocol

[<NoEquality; NoComparison>]
type Endpoint<'In, 'Out> =
    {
        Id: string
        Method: HttpMethod
        Mutates: bool
        Input: Codec<'In>
        Output: Codec<'Out>
    }

type IEndpointVisitor<'Result> =
    abstract Visit<'In, 'Out> : Endpoint<'In, 'Out> -> 'Result

type AnyEndpoint =
    abstract Accept<'Result> : IEndpointVisitor<'Result> -> 'Result

module Api =
    let private endpoint id method mutates input output =
        {
            Id = id
            Method = method
            Mutates = mutates
            Input = input
            Output = output
        }

    let session =
        endpoint
            "session"
            HttpMethod.Get
            false
            RequestCodecs.empty
            (ReplyCodecs.readReply "SessionReply" ResponseCodecs.session)

    let logout =
        endpoint
            "session.logout"
            HttpMethod.Post
            false
            RequestCodecs.empty
            (ReplyCodecs.readReply "LogoutReply" Codec.nullValue)

    let context =
        endpoint
            "context"
            HttpMethod.Post
            false
            RequestCodecs.empty
            (ReplyCodecs.readReply "ContextReply" ResponseCodecs.actorContext)

    let caseGet =
        endpoint
            "case.get"
            HttpMethod.Post
            false
            RequestCodecs.caseGetRequest
            (ReplyCodecs.readReply "CaseReadReply" ResponseCodecs.caseView)

    let caseList =
        endpoint
            "case.list"
            HttpMethod.Post
            false
            RequestCodecs.pageRequest
            (ReplyCodecs.readReply "CasePageReply" ResponseCodecs.casesPage)

    let caseHistory =
        endpoint
            "case.history"
            HttpMethod.Post
            false
            RequestCodecs.historyRequest
            (ReplyCodecs.readReply "HistoryReply" ResponseCodecs.historyPage)

    let operationPreview =
        endpoint
            "operation.preview"
            HttpMethod.Post
            false
            RequestCodecs.operationInput
            ReplyCodecs.preview

    let operationExecute =
        endpoint
            "operation.execute"
            HttpMethod.Post
            true
            RequestCodecs.operationInput
            ReplyCodecs.execute

    let operationObserve =
        endpoint
            "operation.observe"
            HttpMethod.Post
            false
            RequestCodecs.observeRequest
            (ReplyCodecs.readReply "ObservationReply" ReceiptCodecs.receipt)

    let authorityList =
        endpoint
            "authority.list"
            HttpMethod.Post
            false
            RequestCodecs.pageRequest
            (ReplyCodecs.readReply "ActorsReply" ResponseCodecs.actorsPage)

    let authorityGrants =
        endpoint
            "authority.grants"
            HttpMethod.Post
            false
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
            boxed operationObserve
            boxed authorityList
            boxed authorityGrants
        ]

    let id (endpoint: AnyEndpoint) =
        endpoint.Accept
            { new IEndpointVisitor<string> with
                member _.Visit value = value.Id
            }

    let method (endpoint: AnyEndpoint) =
        endpoint.Accept
            { new IEndpointVisitor<HttpMethod> with
                member _.Visit value = value.Method
            }

    let mutates (endpoint: AnyEndpoint) =
        endpoint.Accept
            { new IEndpointVisitor<bool> with
                member _.Visit value = value.Mutates
            }

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
