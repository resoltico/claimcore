namespace ClaimCore.Wire

open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

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
type OperationEcho<'Input, 'Output> =
    {
        Request: 'Input -> System.Guid
        Reply: 'Output -> System.Guid
    }

[<NoEquality; NoComparison>]
type Endpoint<'In, 'Out> =
    {
        Id: EndpointId
        Method: HttpMethod
        Effect: EndpointEffect
        Input: Codec<'In>
        Output: Codec<'Out>
        Classify: 'Out -> ReplyClass
        Correlation: OperationEcho<'In, 'Out> option
        Unavailable: 'In -> 'Out
    }

    member this.Token = EndpointIds.token this.Id

/// Dynamic transport boundaries retain typed values behind the registry's endpoint witness.
type IEndpointInputVisitor<'result> =
    abstract Visit<'input, 'output> : Endpoint<'input, 'output> * 'input -> 'result

type IEndpointInput =
    abstract Accept<'result> : IEndpointInputVisitor<'result> -> 'result

type IEndpointOutputVisitor<'result> =
    abstract Visit<'input, 'output> : Endpoint<'input, 'output> * 'output -> 'result

type IEndpointOutput =
    abstract Class: ReplyClass
    abstract Accept<'result> : IEndpointOutputVisitor<'result> -> 'result

module EndpointValues =
    let operationId (input: IEndpointInput) =
        input.Accept
            { new IEndpointInputVisitor<System.Guid option> with
                member _.Visit<'input, 'output>(endpoint: Endpoint<'input, 'output>, value) =
                    endpoint.Correlation |> Option.map (fun echo -> echo.Request value)
            }

    let echoesOperationId expected (output: IEndpointOutput) =
        output.Accept
            { new IEndpointOutputVisitor<bool> with
                member _.Visit<'input, 'output>(endpoint: Endpoint<'input, 'output>, value) =
                    (endpoint.Correlation |> Option.map (fun echo -> echo.Reply value)) = expected
            }

    let input (endpoint: Endpoint<'input, 'output>) value =
        { new IEndpointInput with
            member _.Accept<'result>(visitor: IEndpointInputVisitor<'result>) =
                visitor.Visit(endpoint, value)
        }

    let output (endpoint: Endpoint<'input, 'output>) value =
        let classification = endpoint.Classify value

        { new IEndpointOutput with
            member _.Class = classification

            member _.Accept<'result>(visitor: IEndpointOutputVisitor<'result>) =
                visitor.Visit(endpoint, value)
        }

    let tryInput (endpoint: Endpoint<'input, 'output>) (value: IEndpointInput) =
        value.Accept
            { new IEndpointInputVisitor<'input option> with
                member _.Visit<'actualInput, 'actualOutput>
                    (actual: Endpoint<'actualInput, 'actualOutput>, value: 'actualInput)
                    =
                    if actual.Id = endpoint.Id && typeof<'input> = typeof<'actualInput> then
                        Some(unbox<'input>(box value))
                    else
                        None
            }

    let tryOutput (endpoint: Endpoint<'input, 'output>) (value: IEndpointOutput) =
        value.Accept
            { new IEndpointOutputVisitor<'output option> with
                member _.Visit<'actualInput, 'actualOutput>
                    (actual: Endpoint<'actualInput, 'actualOutput>, value: 'actualOutput)
                    =
                    if actual.Id = endpoint.Id && typeof<'output> = typeof<'actualOutput> then
                        Some(unbox<'output>(box value))
                    else
                        None
            }

/// A definition preserves its input/output types when a consumer visits the endpoint registry.
type IEndpointVisitor<'Result> =
    abstract Visit<'Input, 'Output> : Endpoint<'Input, 'Output> -> 'Result

type IEndpointDefinition =
    abstract Id: EndpointId
    abstract Accept<'Result> : IEndpointVisitor<'Result> -> 'Result

module EndpointDefinition =
    let create (endpoint: Endpoint<'Input, 'Output>) =
        { new IEndpointDefinition with
            member _.Id = endpoint.Id
            member _.Accept<'Result>(visitor: IEndpointVisitor<'Result>) = visitor.Visit endpoint
        }

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
        ReadInput: JsonValue -> Result<IEndpointInput, Problem>
        ReadOutput: JsonValue -> Result<IEndpointOutput, Problem>
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
            ReadInput =
                fun value ->
                    endpoint.Input.Read "$" value |> Result.map (EndpointValues.input endpoint)
            ReadOutput =
                fun value ->
                    endpoint.Output.Read "$" value |> Result.map (EndpointValues.output endpoint)
            NormalizeInput =
                fun value -> endpoint.Input.Read "$" value |> Result.map endpoint.Input.Write
            ClassifyOutput =
                fun value -> endpoint.Output.Read "$" value |> Result.map endpoint.Classify
            NormalizeOutput =
                fun value -> endpoint.Output.Read "$" value |> Result.map endpoint.Output.Write
        }

    let ofDefinition (definition: IEndpointDefinition) =
        definition.Accept
            { new IEndpointVisitor<Route> with
                member _.Visit<'Input, 'Output>(endpoint: Endpoint<'Input, 'Output>) =
                    describe endpoint
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
            Correlation = None
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
            (ReplyCodecs.readReply "ContextReply" ResponseCodecs.contextReply)

    let workViews =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.WorkViews))
            HttpMethod.Post
            RequestCodecs.empty
            (ReplyCodecs.readReply "WorkViewsReply" ViewCountCodecs.work)

    let caseGet =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseGet))
            HttpMethod.Post
            RequestCodecs.caseGetRequest
            (ReplyCodecs.readReply "CaseReadReply" ResponseCodecs.caseView)

    let caseAssignees =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseAssignees))
            HttpMethod.Post
            RequestCodecs.assigneeRequest
            (ReplyCodecs.readReply "AssigneePageReply" ResponseCodecs.assigneePage)

    let caseList =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseList))
            HttpMethod.Post
            RequestCodecs.caseListRequest
            (ReplyCodecs.readReply "CasePageReply" ResponseCodecs.casesPage)

    let caseSummary =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseSummary))
            HttpMethod.Post
            RequestCodecs.caseSummaryRequest
            (ReplyCodecs.readReply "CaseSummaryReply" CaseTotalsCodecs.summary)

    let caseTimeline =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseTimeline))
            HttpMethod.Post
            RequestCodecs.timelineRequest
            (ReplyCodecs.readReply "CaseTimelineReply" TimelineCodecs.page)

    let reminders =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.ReminderList))
            HttpMethod.Post
            RequestCodecs.reminderListRequest
            (ReplyCodecs.readReply "ReminderListReply" WorkReadCodecs.reminders)

    let reminder =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.ReminderGet))
            HttpMethod.Post
            RequestCodecs.reminderGetRequest
            (ReplyCodecs.readReply "ReminderReadReply" WorkReadCodecs.reminder)

    let operationPreview =
        {
            Id = EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview)
            Method = HttpMethod.Post
            Effect = EndpointEffect.Read
            Input = RequestCodecs.operationInput
            Output = ReplyCodecs.preview
            Classify = OutcomePresentation.classifyPreview
            Correlation =
                Some
                    {
                        Request = OperationInput.operationId
                        Reply = _.OperationId
                    }
            Unavailable =
                fun input ->
                    {
                        PresentedPreviewReply.OperationId = OperationInput.operationId input
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
            Correlation =
                Some
                    {
                        Request = OperationInput.operationId
                        Reply = _.OperationId
                    }
            Unavailable =
                fun input ->
                    {
                        PresentedExecuteReply.OperationId = OperationInput.operationId input
                        Outcome = PresentedExecuteResult.Failed CoreFault.StoreUnavailable
                    }
        }

    let private lookup
        id
        (output: Codec<OperationReply<'result>>)
        (classify: OperationReply<'result> -> ReplyClass)
        : Endpoint<OperationLookup, OperationReply<'result>> =
        {
            Id = id
            Method = HttpMethod.Post
            Effect = EndpointEffect.Read
            Input = RequestCodecs.operationGetRequest
            Output = output
            Classify = classify
            Correlation =
                Some
                    {
                        Request = _.OperationId
                        Reply = _.OperationId
                    }
            Unavailable =
                fun input ->
                    {
                        OperationReply.OperationId = input.OperationId
                        Outcome = Outcome.Failed CoreFault.StoreUnavailable
                    }
        }

    let operationGet =
        lookup
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.OperationGet))
            OperationLookupCodecs.reply
            (fun reply -> OutcomePresentation.classifyOperationGet reply.Outcome)

    let authorityList =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityList))
            HttpMethod.Post
            RequestCodecs.actorListRequest
            (ReplyCodecs.readReply "ActorsReply" ResponseCodecs.actorsPage)

    let authorityGrants =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityGrants))
            HttpMethod.Post
            RequestCodecs.grantsRequest
            (ReplyCodecs.readReply "GrantsReply" ResponseCodecs.grantsPage)

    let authorityHistory =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityHistory))
            HttpMethod.Post
            RequestCodecs.authorityHistoryRequest
            (ReplyCodecs.readReply "AuthorityHistoryReply" AuthorityHistoryCodecs.page)

    let authorityRequests =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityRequests))
            HttpMethod.Post
            RequestCodecs.accessRequestsRequest
            (ReplyCodecs.readReply "AccessRequestsReply" ResponseCodecs.accessRequestsPage)

    let authorityRequestsDismiss =
        { read
              (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityRequestsDismiss))
              HttpMethod.Post
              RequestCodecs.accessRequestDismissRequest
              (ReplyCodecs.readReply "AccessRequestDismissReply" Codec.nullValue) with
            Effect = EndpointEffect.OperationalWrite
        }

    let private defineHost =
        function
        | HostEndpoint.Session -> EndpointDefinition.create session
        | HostEndpoint.Logout -> EndpointDefinition.create logout

    // This total endpoint-definition table preserves every typed registry witness; splitting it would hide missing endpoint coverage.
    let private defineActor =
        function
        | (ActorEndpoint.Query QueryEndpoint.ReminderList) -> EndpointDefinition.create reminders
        | (ActorEndpoint.Query QueryEndpoint.ReminderGet) -> EndpointDefinition.create reminder
        | (ActorEndpoint.Query QueryEndpoint.CaseTimeline) -> EndpointDefinition.create caseTimeline
        | (ActorEndpoint.Query QueryEndpoint.Context) -> EndpointDefinition.create context
        | (ActorEndpoint.Query QueryEndpoint.CaseGet) -> EndpointDefinition.create caseGet
        | (ActorEndpoint.Query QueryEndpoint.CaseAssignees) ->
            EndpointDefinition.create caseAssignees
        | (ActorEndpoint.Query QueryEndpoint.CaseList) -> EndpointDefinition.create caseList
        | (ActorEndpoint.Query QueryEndpoint.WorkViews) -> EndpointDefinition.create workViews
        | (ActorEndpoint.Query QueryEndpoint.CaseSummary) -> EndpointDefinition.create caseSummary
        | (ActorEndpoint.Operation OperationEndpoint.Preview) ->
            EndpointDefinition.create operationPreview
        | (ActorEndpoint.Operation OperationEndpoint.Execute) ->
            EndpointDefinition.create operationExecute
        | (ActorEndpoint.Query QueryEndpoint.OperationGet) -> EndpointDefinition.create operationGet
        | (ActorEndpoint.Query QueryEndpoint.AuthorityList) ->
            EndpointDefinition.create authorityList
        | (ActorEndpoint.Query QueryEndpoint.AuthorityGrants) ->
            EndpointDefinition.create authorityGrants
        | (ActorEndpoint.Query QueryEndpoint.AuthorityHistory) ->
            EndpointDefinition.create authorityHistory
        | (ActorEndpoint.Query QueryEndpoint.AuthorityRequests) ->
            EndpointDefinition.create authorityRequests
        | (ActorEndpoint.Query QueryEndpoint.AuthorityRequestsDismiss) ->
            EndpointDefinition.create authorityRequestsDismiss

    let definitions =
        (HostEndpoint.all |> List.map defineHost)
        @ (ActorEndpoint.all |> List.map defineActor)

    let forHost host =
        definitions
        |> List.find (fun definition -> definition.Id = EndpointId.Host host)
        |> Route.ofDefinition

    let forActor actor =
        definitions
        |> List.find (fun definition -> definition.Id = EndpointId.Actor actor)
        |> Route.ofDefinition

    let all =
        (HostEndpoint.all |> List.map forHost)
        @ (ActorEndpoint.all |> List.map forActor)

    let identity (endpoint: Route) = endpoint.Id
    let id (endpoint: Route) = endpoint.Token
    let path endpoint = "/api/" + id endpoint
    let method (endpoint: Route) = endpoint.Method
    let effect (endpoint: Route) = endpoint.Effect

    let requiresInstallation (endpoint: Route) =
        match endpoint.Id with
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.OperationGet) -> true
        | _ -> false

    let executesLedger endpoint =
        effect endpoint = EndpointEffect.LedgerExecution

    let tryFind name =
        all |> List.tryFind (fun endpoint -> id endpoint = name)

    let admitInput (endpoint: Route) value =
        endpoint.NormalizeInput value |> Result.map ignore

    let admitOutput (endpoint: Route) value =
        endpoint.NormalizeOutput value |> Result.map ignore
