namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Contract
open ClaimCore.Domain

[<NoEquality; NoComparison>]
type Handler<'Input, 'Output> =
    {
        Endpoint: Endpoint<'Input, 'Output>
        Invoke: HandlerContext -> 'Input -> CancellationToken -> Task<'Output>
        Unavailable: 'Input -> 'Output
    }

type IHandlerVisitor<'Result> =
    abstract Visit<'Input, 'Output> : Handler<'Input, 'Output> -> 'Result

type AnyHandler =
    abstract Accept<'Result> : IHandlerVisitor<'Result> -> 'Result

module Handlers =
    let internal context installationId store calendar principal =
        if installationId = Guid.Empty then
            invalidArg (nameof installationId) "Use an admitted installation."

        {
            InstallationId = installationId
            Store = store
            Calendar = calendar
            Principal = principal
        }

    let define endpoint unavailable invoke : AnyHandler =
        let handler =
            {
                Endpoint = endpoint
                Unavailable = unavailable
                Invoke = invoke
            }

        { new AnyHandler with
            member _.Accept visitor = visitor.Visit handler
        }

    let read endpoint invoke =
        define endpoint (fun _ -> ReadReply.Failed CoreFault.StoreUnavailable) invoke

    let forActor =
        function
        | ActorEndpoint.Context -> read Api.context Core.context
        | ActorEndpoint.CaseGet -> read Api.caseGet Core.get
        | ActorEndpoint.CaseList -> read Api.caseList Core.list
        | ActorEndpoint.CaseHistory -> read Api.caseHistory Core.history
        | ActorEndpoint.Observe -> read Api.operationObserve Core.observe
        | ActorEndpoint.AuthorityList -> read Api.authorityList Core.actors
        | ActorEndpoint.AuthorityGrants -> read Api.authorityGrants Core.grants
        | ActorEndpoint.Preview ->
            define
                Api.operationPreview
                (fun input ->
                    {
                        PreviewReply.OperationId = OperationInput.operationId input
                        Outcome = PreviewResult.Failed CoreFault.StoreUnavailable
                    })
                Core.previewReply
        | ActorEndpoint.Execute ->
            define
                Api.operationExecute
                (fun input ->
                    {
                        ExecuteReply.OperationId = OperationInput.operationId input
                        Outcome = ExecuteResult.Failed CoreFault.StoreUnavailable
                    })
                Core.execute
        | ActorEndpoint.Resolve ->
            define
                Api.operationResolve
                (fun _ -> ResolveReply.Failed CoreFault.StoreUnavailable)
                Core.resolve

    let endpoint (handler: AnyHandler) =
        handler.Accept
            { new IHandlerVisitor<AnyEndpoint> with
                member _.Visit handler =
                    { new AnyEndpoint with
                        member _.Accept visitor = visitor.Visit handler.Endpoint
                    }
            }

    let call
        (endpoint: Endpoint<'Input, 'Output>)
        context
        (input: 'Input)
        cancellation
        : Task<'Output> =
        let actor =
            match endpoint.Id with
            | EndpointId.Actor actor -> actor
            | EndpointId.Host _ ->
                invalidArg (nameof endpoint) "Host endpoints have no Application handler."

        (forActor actor).Accept
            { new IHandlerVisitor<Task<'Output>> with
                member _.Visit handler =
                    // Reference identity witnesses equality of the erased generic types.
                    // A record with a forged identity cannot reach either cast.
                    if not (obj.ReferenceEquals(endpoint, handler.Endpoint)) then
                        invalidArg (nameof endpoint) "Use the registered typed endpoint."

                    task {
                        let! reply = handler.Invoke context (unbox (box input)) cancellation
                        return unbox (box reply)
                    }
            }
