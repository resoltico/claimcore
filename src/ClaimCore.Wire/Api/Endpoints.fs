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
        ValidateInput: JsonValue -> Result<unit, Problem>
        ValidateOutput: JsonValue -> Result<unit, Problem>
        NormalizeInput: JsonValue -> Result<JsonValue, Problem>
        NormalizeOutput: JsonValue -> Result<JsonValue, Problem>
        ClassifyOutput: JsonValue -> Result<ReplyClass, Problem>
    }

    member this.Token = EndpointIds.token this.Id

module Route =
    let describe (endpoint: Endpoint<'Input, 'Output>) =
        let readInput value =
            endpoint.Input.Validate "$" value
            |> Result.bind (fun () -> endpoint.Input.Read "$" value)

        let readOutput value =
            endpoint.Output.Validate "$" value
            |> Result.bind (fun () -> endpoint.Output.Read "$" value)

        {
            Id = endpoint.Id
            Method = endpoint.Method
            Effect = endpoint.Effect
            InputSchema = endpoint.Input.Schema
            OutputSchema = endpoint.Output.Schema
            InputDefinitions = endpoint.Input.Definitions
            OutputDefinitions = endpoint.Output.Definitions
            ReadInput = fun value -> readInput value |> Result.map (EndpointValues.input endpoint)
            ReadOutput =
                fun value -> readOutput value |> Result.map (EndpointValues.output endpoint)
            ValidateInput = endpoint.Input.Validate "$"
            ValidateOutput = endpoint.Output.Validate "$"
            NormalizeInput = fun value -> readInput value |> Result.map endpoint.Input.Write
            ClassifyOutput = fun value -> readOutput value |> Result.map endpoint.Classify
            NormalizeOutput = fun value -> readOutput value |> Result.map endpoint.Output.Write
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

    let private actorRead query input name data =
        {
            Id = EndpointId.Actor(ActorEndpoint.Query query)
            Method = HttpMethod.Post
            Effect = EndpointEffect.Read
            Input = input
            Output =
                RepresentationCodecs.reply
                    name
                    HumanDocumentCodecs.readReply
                    (ReplyCodecs.dataReadReply (name + ".DATA_RESULT") data)
            Classify =
                function
                | RepresentationReply.Human reply -> HumanReadReply.classify reply
                | RepresentationReply.Data reply -> OutcomePresentation.classify reply
            Correlation = None
            Unavailable =
                fun _ -> RepresentationReply.Data(Outcome.Failed CoreFault.StoreUnavailable)
        }

    let context =
        actorRead QueryTaskId.Context RequestCodecs.empty "ContextReply" ResponseCodecs.contextReply

    let workViews =
        actorRead QueryTaskId.WorkViews RequestCodecs.empty "WorkViewsReply" ViewCountCodecs.work

    let caseGet =
        actorRead
            QueryTaskId.CaseGet
            RequestCodecs.caseGetRequest
            "CaseReadReply"
            ResponseCodecs.caseView

    let caseAssignees =
        actorRead
            QueryTaskId.CaseAssignees
            RequestCodecs.assigneeRequest
            "AssigneePageReply"
            ResponseCodecs.assigneePage

    let caseList =
        actorRead
            QueryTaskId.CaseList
            RequestCodecs.caseListRequest
            "CasePageReply"
            ResponseCodecs.casesPage

    let caseSummary =
        actorRead
            QueryTaskId.CaseSummary
            RequestCodecs.caseSummaryRequest
            "CaseSummaryReply"
            CaseTotalsCodecs.summary

    let caseTimeline =
        actorRead
            QueryTaskId.CaseTimeline
            RequestCodecs.timelineRequest
            "CaseTimelineReply"
            TimelineCodecs.page

    let casePayments =
        actorRead
            QueryTaskId.CasePayments
            RequestCodecs.casePaymentsRequest
            "CasePaymentsReply"
            PaymentPageCodecs.page

    let caseNotes =
        actorRead
            QueryTaskId.CaseNotes
            RequestCodecs.caseNotesRequest
            "CaseNotesReply"
            WorkReadCodecs.notes

    let reminders =
        actorRead
            QueryTaskId.ReminderList
            RequestCodecs.reminderListRequest
            "ReminderListReply"
            WorkReadCodecs.reminders

    let reminder =
        actorRead
            QueryTaskId.ReminderGet
            RequestCodecs.reminderGetRequest
            "ReminderReadReply"
            WorkReadCodecs.reminder

    let operationPreviewUnavailable input : PreviewReply =
        {
            OperationId = OperationInput.operationId input
            Outcome = Outcome.Failed CoreFault.StoreUnavailable
        }

    let operationExecuteUnavailable input : ExecuteReply =
        {
            OperationId = OperationInput.operationId input
            Outcome = ExecuteResult.Failed CoreFault.StoreUnavailable
        }

    let operationGetUnavailable (input: OperationLookup) : OperationReply<OperationLookupResult> =
        {
            OperationId = input.OperationId
            Outcome = Outcome.Failed CoreFault.StoreUnavailable
        }

    let operationPreview =
        {
            Id = EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview)
            Method = HttpMethod.Post
            Effect = EndpointEffect.Read
            Input = RequestCodecs.operationInput
            Output =
                RepresentationCodecs.reply
                    "PreviewReply"
                    HumanOperationCodecs.preview
                    ReplyCodecs.preview
            Classify =
                RepresentationReply.fold
                    HumanOperationReply.classifyPreview
                    OutcomePresentation.classifyPreview
            Correlation =
                Some
                    {
                        Request = OperationInput.operationId
                        Reply = RepresentationReply.fold _.OperationId _.OperationId
                    }
            Unavailable = operationPreviewUnavailable >> RepresentationReply.Data
        }

    let operationExecute =
        {
            Id = EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute)
            Method = HttpMethod.Post
            Effect = EndpointEffect.LedgerExecution
            Input = RequestCodecs.operationInput
            Output =
                RepresentationCodecs.reply
                    "ExecuteReply"
                    HumanOperationCodecs.execute
                    ReplyCodecs.execute
            Classify =
                RepresentationReply.fold HumanOperationReply.classifyExecute (fun reply ->
                    OutcomePresentation.classifyExecute reply.Outcome)
            Correlation =
                Some
                    {
                        Request = OperationInput.operationId
                        Reply = RepresentationReply.fold _.OperationId _.OperationId
                    }
            Unavailable = operationExecuteUnavailable >> RepresentationReply.Data
        }

    let operationGet =
        {
            Id = EndpointId.Actor(ActorEndpoint.Query QueryTaskId.OperationGet)
            Method = HttpMethod.Post
            Effect = EndpointEffect.Read
            Input = RequestCodecs.operationGetRequest
            Output =
                RepresentationCodecs.reply
                    "OperationGetReply"
                    HumanOperationCodecs.lookup
                    OperationLookupCodecs.reply
            Classify =
                RepresentationReply.fold HumanOperationReply.classifyLookup (fun reply ->
                    OutcomePresentation.classifyOperationGet reply.Outcome)
            Correlation =
                Some
                    {
                        Request = _.OperationId
                        Reply = RepresentationReply.fold _.OperationId _.OperationId
                    }
            Unavailable = operationGetUnavailable >> RepresentationReply.Data
        }

    let authorityList =
        actorRead
            QueryTaskId.AuthorityList
            RequestCodecs.actorListRequest
            "ActorsReply"
            ResponseCodecs.actorsPage

    let authorityGrants =
        actorRead
            QueryTaskId.AuthorityGrants
            RequestCodecs.grantsRequest
            "GrantsReply"
            ResponseCodecs.grantsPage

    let authorityHistory =
        actorRead
            QueryTaskId.AuthorityHistory
            RequestCodecs.authorityHistoryRequest
            "AuthorityHistoryReply"
            AuthorityHistoryCodecs.page

    let authorityRequests =
        actorRead
            QueryTaskId.AuthorityRequests
            RequestCodecs.accessRequestsRequest
            "AccessRequestsReply"
            ResponseCodecs.accessRequestsPage

    let authorityRequestsDismiss =
        { actorRead
              QueryTaskId.AuthorityRequestsDismiss
              RequestCodecs.accessRequestDismissRequest
              "AccessRequestDismissReply"
              Codec.nullValue with
            Effect = EndpointEffect.OperationalWrite
        }

    let private defineHost =
        function
        | HostEndpoint.Session -> EndpointDefinition.create session
        | HostEndpoint.Logout -> EndpointDefinition.create logout

    // This total endpoint-definition table preserves every typed registry witness; splitting it would hide missing endpoint coverage.
    let private defineActor =
        function
        | (ActorEndpoint.Query QueryTaskId.CasePayments) -> EndpointDefinition.create casePayments
        | (ActorEndpoint.Query QueryTaskId.CaseNotes) -> EndpointDefinition.create caseNotes
        | (ActorEndpoint.Query QueryTaskId.ReminderList) -> EndpointDefinition.create reminders
        | (ActorEndpoint.Query QueryTaskId.ReminderGet) -> EndpointDefinition.create reminder
        | (ActorEndpoint.Query QueryTaskId.CaseTimeline) -> EndpointDefinition.create caseTimeline
        | (ActorEndpoint.Query QueryTaskId.Context) -> EndpointDefinition.create context
        | (ActorEndpoint.Query QueryTaskId.CaseGet) -> EndpointDefinition.create caseGet
        | (ActorEndpoint.Query QueryTaskId.CaseAssignees) -> EndpointDefinition.create caseAssignees
        | (ActorEndpoint.Query QueryTaskId.CaseList) -> EndpointDefinition.create caseList
        | (ActorEndpoint.Query QueryTaskId.WorkViews) -> EndpointDefinition.create workViews
        | (ActorEndpoint.Query QueryTaskId.CaseSummary) -> EndpointDefinition.create caseSummary
        | (ActorEndpoint.Operation OperationEndpoint.Preview) ->
            EndpointDefinition.create operationPreview
        | (ActorEndpoint.Operation OperationEndpoint.Execute) ->
            EndpointDefinition.create operationExecute
        | (ActorEndpoint.Query QueryTaskId.OperationGet) -> EndpointDefinition.create operationGet
        | (ActorEndpoint.Query QueryTaskId.AuthorityList) -> EndpointDefinition.create authorityList
        | (ActorEndpoint.Query QueryTaskId.AuthorityGrants) ->
            EndpointDefinition.create authorityGrants
        | (ActorEndpoint.Query QueryTaskId.AuthorityHistory) ->
            EndpointDefinition.create authorityHistory
        | (ActorEndpoint.Query QueryTaskId.AuthorityRequests) ->
            EndpointDefinition.create authorityRequests
        | (ActorEndpoint.Query QueryTaskId.AuthorityRequestsDismiss) ->
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

    let requiresRepresentation (endpoint: Route) =
        match endpoint.Id with
        | EndpointId.Actor _ -> true
        | EndpointId.Host _ -> false

    let requiresInstallation (endpoint: Route) =
        match endpoint.Id with
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute)
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.OperationGet) -> true
        | _ -> false

    let executesLedger endpoint =
        effect endpoint = EndpointEffect.LedgerExecution

    let tryFind name =
        all |> List.tryFind (fun endpoint -> id endpoint = name)

    let admitInput (endpoint: Route) value =
        endpoint.NormalizeInput value |> Result.map ignore

    let admitOutput (endpoint: Route) value =
        endpoint.NormalizeOutput value |> Result.map ignore
