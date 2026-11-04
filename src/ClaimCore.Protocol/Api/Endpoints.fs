namespace ClaimCore.Protocol

open ClaimCore.Protocol

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
            (ReplyCodecs.readReply "ContextReply" ResponseCodecs.contextReply)

    let caseViews =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseViews))
            HttpMethod.Post
            RequestCodecs.empty
            (ReplyCodecs.readReply "CaseViewsReply" ViewCountCodecs.cases)

    let reminderViews =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.ReminderViews))
            HttpMethod.Post
            RequestCodecs.empty
            (ReplyCodecs.readReply "ReminderViewsReply" ViewCountCodecs.reminders)

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

    let caseHistory =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseHistory))
            HttpMethod.Post
            RequestCodecs.historyRequest
            (ReplyCodecs.readReply
                "HistoryReply"
                (ResponseCodecs.historyPageFor ReceiptCodecs.caseReceipt))

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

    let workOverview =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.WorkOverview))
            HttpMethod.Post
            RequestCodecs.workOverviewRequest
            (ReplyCodecs.readReply "WorkOverviewReply" WorkReadCodecs.overview)

    let caseActivity =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseActivity))
            HttpMethod.Post
            RequestCodecs.caseActivityRequest
            (ReplyCodecs.readReply "CaseActivityReply" WorkReadCodecs.activityPage)

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

    let private lookup id output classify =
        {
            Id = id
            Method = HttpMethod.Post
            Effect = EndpointEffect.Read
            Input = RequestCodecs.observeRequest
            Output = output
            Classify = classify
            Correlation = fun input -> Some input.OperationId
            Unavailable =
                fun input ->
                    {
                        OperationReply.OperationId = input.OperationId
                        Outcome = Outcome.Failed CoreFault.StoreUnavailable
                    }
        }

    let operationStatus =
        { lookup
              (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Status))
              ResolutionCodecs.status
              (fun reply -> OutcomePresentation.classifyStatus reply.Outcome) with
            Input = RequestCodecs.statusRequest
            Classify = fun reply -> OutcomePresentation.classifyStatus reply.Outcome
            Correlation = fun input -> Some input.OperationId
        }

    let operationResolve =
        { lookup
              (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Resolve))
              ResolutionCodecs.reply
              (fun reply -> OutcomePresentation.classifyResolve reply.Outcome) with
            Input = RequestCodecs.resolveRequest
            Effect = EndpointEffect.OperationalWrite
            Classify = fun reply -> OutcomePresentation.classifyResolve reply.Outcome
            Correlation = fun input -> Some input.OperationId
        }

    let operationObserve =
        { lookup
              (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Observe))
              ReplyCodecs.observation
              (fun reply -> OutcomePresentation.classify reply.Outcome) with
            Classify = fun reply -> OutcomePresentation.classify reply.Outcome
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

    let authorityHistory =
        read
            (EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityHistory))
            HttpMethod.Post
            RequestCodecs.authorityHistoryRequest
            (ReplyCodecs.readReply "AuthorityHistoryReply" AuthorityHistoryCodecs.page)

    let private defineHost =
        function
        | HostEndpoint.Session -> EndpointDefinition.create session
        | HostEndpoint.Logout -> EndpointDefinition.create logout

    // This total endpoint-definition table preserves every typed registry witness; splitting it would hide missing endpoint coverage.
    // fsharplint:disable-next-line CyclomaticComplexity
    let private defineActor =
        function
        | (ActorEndpoint.Query QueryEndpoint.ReminderList) -> EndpointDefinition.create reminders
        | (ActorEndpoint.Query QueryEndpoint.ReminderGet) -> EndpointDefinition.create reminder
        | (ActorEndpoint.Query QueryEndpoint.WorkOverview) -> EndpointDefinition.create workOverview
        | (ActorEndpoint.Query QueryEndpoint.CaseActivity) -> EndpointDefinition.create caseActivity
        | (ActorEndpoint.Query QueryEndpoint.CaseTimeline) -> EndpointDefinition.create caseTimeline
        | (ActorEndpoint.Query QueryEndpoint.Context) -> EndpointDefinition.create context
        | (ActorEndpoint.Query QueryEndpoint.CaseGet) -> EndpointDefinition.create caseGet
        | (ActorEndpoint.Query QueryEndpoint.CaseAssignees) ->
            EndpointDefinition.create caseAssignees
        | (ActorEndpoint.Query QueryEndpoint.CaseList) -> EndpointDefinition.create caseList
        | (ActorEndpoint.Query QueryEndpoint.CaseViews) -> EndpointDefinition.create caseViews
        | (ActorEndpoint.Query QueryEndpoint.ReminderViews) ->
            EndpointDefinition.create reminderViews
        | (ActorEndpoint.Query QueryEndpoint.CaseSummary) -> EndpointDefinition.create caseSummary
        | (ActorEndpoint.Query QueryEndpoint.CaseHistory) -> EndpointDefinition.create caseHistory
        | (ActorEndpoint.Operation OperationEndpoint.Preview) ->
            EndpointDefinition.create operationPreview
        | (ActorEndpoint.Operation OperationEndpoint.Execute) ->
            EndpointDefinition.create operationExecute
        | (ActorEndpoint.Query QueryEndpoint.Resolve) -> EndpointDefinition.create operationResolve
        | (ActorEndpoint.Query QueryEndpoint.Status) -> EndpointDefinition.create operationStatus
        | (ActorEndpoint.Query QueryEndpoint.Observe) -> EndpointDefinition.create operationObserve
        | (ActorEndpoint.Query QueryEndpoint.AuthorityList) ->
            EndpointDefinition.create authorityList
        | (ActorEndpoint.Query QueryEndpoint.AuthorityGrants) ->
            EndpointDefinition.create authorityGrants
        | (ActorEndpoint.Query QueryEndpoint.AuthorityHistory) ->
            EndpointDefinition.create authorityHistory

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
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Resolve)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Status) -> true
        | _ -> false

    let executesLedger endpoint =
        effect endpoint = EndpointEffect.LedgerExecution

    let tryFind name =
        all |> List.tryFind (fun endpoint -> id endpoint = name)

    let admitInput (endpoint: Route) value =
        endpoint.NormalizeInput value |> Result.map ignore

    let admitOutput (endpoint: Route) value =
        endpoint.NormalizeOutput value |> Result.map ignore

/// Transport identity admission follows structural decoding and precedes any client certainty.
module ReplyCorrelation =
    let private memberValue name value = JsonValue.tryMember name value

    let private text =
        function
        | JsonValue.String value -> Some value
        | _ -> None

    let private stringMember name value =
        memberValue name value |> Option.bind text

    let private guid name value =
        memberValue name value
        |> Option.bind (fun value -> Codec.uuid.Read "$" value |> Result.toOption)

    let private sameReference left right =
        match left, right with
        | Some left, Some right -> CaseReference.key left = CaseReference.key right
        | _ -> false

    let private caseTarget input actual =
        match memberValue "target" input with
        | None -> false
        | Some selector ->
            match stringMember "kind" selector with
            | Some "REFERENCE" ->
                sameReference
                    (stringMember "caseReference" selector)
                    (stringMember "caseReference" actual)
            | Some "ID" ->
                let expected = guid "caseId" selector
                expected.IsSome && guid "caseId" actual = expected
            | _ -> false

    let private integerMember name value =
        memberValue name value
        |> Option.bind (function
            | JsonValue.Integer value -> Some value
            | _ -> None)

    let private reminderEffects preview input receipt =
        let act = memberValue "act" input
        let tag = act |> Option.bind (stringMember "kind")
        let expectedId = act |> Option.bind (guid "reminderId")
        let expectedRevision = act |> Option.bind (integerMember "expectedRevision")
        let successor = expectedRevision |> Option.map ((+) 1L)
        let operation = guid "operationId" input
        let effects = memberValue "effects" receipt

        let version (expectedId: System.Guid option) (expectedRevision: int64 option) value =
            expectedId.IsSome
            && guid "reminderId" value = expectedId
            && expectedRevision.IsSome
            && integerMember "revision" value = expectedRevision

        match tag, effects with
        | Some "COMPLETE_AND_PLAN_NEXT", Some effects ->
            stringMember "kind" effects = Some "COMPLETED_AND_PLANNED"
            && (memberValue "completed" effects |> Option.exists (version expectedId successor))
            && (memberValue "next" effects |> Option.exists (version operation (Some 1L)))
        | Some tag, Some effects ->
            stringMember "kind" effects =
                Some(
                    if preview then
                        (if tag = "CREATE" then "CREATED" else "CHANGED")
                    else
                        "SINGLE"
                )
            && (memberValue "reminder" effects
                |> Option.exists (
                    version
                        (if tag = "CREATE" then operation else expectedId)
                        (if tag = "CREATE" then Some 1L else successor)
                ))
        | _ -> false

    let private newFamilyReceipt input subject =
        let family = stringMember "kind" input
        let act = memberValue "act" input |> Option.bind (stringMember "kind")

        stringMember "kind" subject = family
        && caseTarget input subject
        && stringMember "act" subject = act
        && (match family with
            | Some "WORK" ->
                integerMember "revision" subject =
                    (integerMember "expectedRevision" input |> Option.map ((+) 1L))
            | Some "REMINDER" -> reminderEffects false input subject
            | _ -> false)

    let private observedParent input subject =
        memberValue "parent" subject
        |> Option.exists (fun parent ->
            caseTarget input parent
            && sameReference
                (stringMember "caseReference" parent)
                (stringMember "caseReference" subject)
            && (match stringMember "kind" subject with
                | Some "WORK" ->
                    memberValue "proposed" subject
                    |> Option.exists (fun work -> guid "caseId" work = guid "caseId" parent)
                | Some "REMINDER" -> guid "caseId" subject = guid "caseId" parent
                | _ -> false))

    let matches token input reply =
        let outcome = memberValue "outcome" reply
        let kind = outcome |> Option.bind (stringMember "kind")
        let data = outcome |> Option.bind (memberValue "data")
        let operation = guid "operationId" input

        let operationEndpoint =
            List.contains
                token
                [
                    "operation.execute"
                    "operation.preview"
                    "operation.observe"
                    "operation.status"
                    "operation.resolve"
                ]

        let envelope =
            not operationEndpoint
            || (operation.IsSome && guid "operationId" reply = operation)

        let receipt =
            match kind, token with
            | Some("ACCEPTED" | "ALREADY_ACCEPTED"), _
            | Some "SUCCEEDED", "operation.observe" -> data
            | _ -> None

        let receiptIdentity =
            receipt |> Option.forall (fun value -> guid "operationId" value = operation)

        let receiptTarget =
            receipt
            |> Option.forall (fun receipt ->
                match stringMember "kind" input, memberValue "subject" receipt with
                | Some "CASE", Some subject ->
                    stringMember "kind" subject = Some "CASE"
                    && caseTarget input subject
                    && stringMember "act" subject =
                        (memberValue "act" input |> Option.bind (stringMember "kind"))
                | Some("WORK" | "REMINDER"), Some subject -> newFamilyReceipt input subject
                | Some("CASE" | "WORK" | "REMINDER"), None -> false
                | _ -> true)

        let readTarget =
            match token, kind, data with
            | ("case.get" | "case.history" | "case.activity"), Some "SUCCEEDED", Some value ->
                caseTarget input value
            | "reminder.get", Some "SUCCEEDED", Some value ->
                memberValue "item" value
                |> Option.exists (fun item ->
                    (memberValue "reminder" item
                     |> Option.exists (fun reminder ->
                         let actual =
                             match reminder with
                             | JsonValue.Object fields ->
                                 JsonValue.Object(
                                     ("caseReference",
                                      memberValue "caseReference" item
                                      |> Option.defaultValue JsonValue.Null)
                                     :: fields
                                 )
                             | _ -> reminder

                         caseTarget input actual
                         && guid "reminderId" reminder = guid "reminderId" input
                         && (match
                                 memberValue "target" input |> Option.bind (stringMember "kind")
                             with
                             | Some "ID" ->
                                 memberValue "target" input
                                 |> Option.exists (fun selector ->
                                     guid "caseId" selector = guid "caseId" reminder)
                             | _ -> true))))
            | "authority.grants", Some "SUCCEEDED", Some value ->
                memberValue "actor" value
                |> Option.exists (fun actor -> guid "actorId" actor = guid "actorId" input)
            | _ -> true

        let previewTarget =
            match token, kind, stringMember "kind" input, data with
            | "operation.preview", Some "REVIEWED", Some "CASE", Some data ->
                memberValue "review" data
                |> Option.bind (memberValue "subject")
                |> Option.bind (memberValue "after")
                |> Option.exists (caseTarget input)
            | "operation.preview", Some "REVIEWED", Some "WORK", Some data ->
                memberValue "review" data
                |> Option.bind (memberValue "subject")
                |> Option.exists (fun subject ->
                    stringMember "kind" subject = Some "WORK"
                    && observedParent input subject
                    && (memberValue "proposed" subject
                        |> Option.exists (fun proposed ->
                            let actual =
                                match proposed with
                                | JsonValue.Object fields ->
                                    JsonValue.Object(
                                        ("caseReference",
                                         memberValue "caseReference" subject
                                         |> Option.defaultValue JsonValue.Null)
                                        :: fields
                                    )
                                | _ -> proposed

                            caseTarget input actual
                            && integerMember "revision" proposed =
                                (integerMember "expectedRevision" input |> Option.map ((+) 1L)))))
            | "operation.preview", Some "REVIEWED", Some "REMINDER", Some data ->
                memberValue "review" data
                |> Option.bind (memberValue "subject")
                |> Option.exists (fun subject ->
                    stringMember "kind" subject = Some "REMINDER"
                    && observedParent input subject
                    && caseTarget input subject
                    && reminderEffects true input subject)
            | _ -> true

        envelope && receiptIdentity && receiptTarget && readTarget && previewTarget
