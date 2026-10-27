namespace ClaimCore.Application

open ClaimCore.Contract.Protocol

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Contract
open ClaimCore.Domain

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

    let route =
        function
        | ActorEndpoint.Context -> Route.read Api.context Core.context
        | ActorEndpoint.CaseGet -> Route.read Api.caseGet Core.get
        | ActorEndpoint.CaseList -> Route.read Api.caseList Core.list
        | ActorEndpoint.CaseHistory -> Route.read Api.caseHistory Core.history
        | ActorEndpoint.Observe -> Route.read Api.operationObserve Core.observe
        | ActorEndpoint.AuthorityList -> Route.read Api.authorityList Core.actors
        | ActorEndpoint.AuthorityGrants -> Route.read Api.authorityGrants Core.grants
        | ActorEndpoint.Preview ->
            Route.bind
                Api.operationPreview
                (fun input ->
                    {
                        PreviewReply.OperationId = OperationInput.operationId input
                        Outcome = PreviewResult.Failed CoreFault.StoreUnavailable
                    })
                Core.previewReply
        | ActorEndpoint.Execute ->
            Route.bind
                Api.operationExecute
                (fun input ->
                    {
                        ExecuteReply.OperationId = OperationInput.operationId input
                        Outcome = ExecuteResult.Failed CoreFault.StoreUnavailable
                    })
                Core.execute
        | ActorEndpoint.Resolve ->
            Route.bind
                Api.operationResolve
                (fun _ -> ResolveReply.Failed CoreFault.StoreUnavailable)
                Core.resolve
