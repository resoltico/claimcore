namespace ClaimCore.Web

open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Application


open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Web.Composition

module internal ApiRoutes =
    // This total endpoint-to-handler table has no nested decisions; keeping it exhaustive makes missing transport bindings compiler errors.
    // fsharplint:disable-next-line CyclomaticComplexity
    let route =
        function
        | ActorEndpoint.Query QueryEndpoint.Context -> Route.bind Api.context Service.context
        | ActorEndpoint.Query QueryEndpoint.CaseGet -> Route.bind Api.caseGet Service.get
        | ActorEndpoint.Query QueryEndpoint.CaseAssignees ->
            Route.bind Api.caseAssignees Service.assignees
        | ActorEndpoint.Query QueryEndpoint.CaseList -> Route.bind Api.caseList Service.list
        | ActorEndpoint.Query QueryEndpoint.CaseSummary ->
            Route.bind Api.caseSummary Service.caseSummary
        | ActorEndpoint.Query QueryEndpoint.CaseHistory ->
            Route.bind Api.caseHistory Service.history
        | ActorEndpoint.Query QueryEndpoint.Observe ->
            Route.bind Api.operationObserve Service.observe
        | ActorEndpoint.Query QueryEndpoint.AuthorityList ->
            Route.bind Api.authorityList Service.actors
        | ActorEndpoint.Query QueryEndpoint.AuthorityGrants ->
            Route.bind Api.authorityGrants Service.grants
        | ActorEndpoint.Query QueryEndpoint.AuthorityHistory ->
            Route.bind Api.authorityHistory Service.authorityHistory
        | ActorEndpoint.Operation OperationEndpoint.Preview ->
            Route.bind Api.operationPreview Service.preview
        | ActorEndpoint.Operation OperationEndpoint.Execute ->
            Route.bind Api.operationExecute Service.execute
        | ActorEndpoint.Query QueryEndpoint.Status -> Route.bind Api.operationStatus Service.status
        | ActorEndpoint.Query QueryEndpoint.Resolve ->
            Route.bind Api.operationResolve Service.resolve

    let handshake (context: HttpContext) =
        match context.Request.Headers.TryGetValue "ClaimCore-Contract" with
        | true, values when values.Count = 1 -> string values[0] = ContractIdentity.value
        | _ -> false

    let ingress (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            match
                Api.all
                |> List.tryFind (fun endpoint ->
                    Some(Api.path endpoint) = Option.ofObj context.Request.Path.Value)
            with
            | Some endpoint when context.Request.Method <> (Api.method endpoint).Method ->
                do!
                    (RouteSupport.hostFailure context HostFailureReason.MethodRejected).ExecuteAsync
                        context
            | _ -> do! next.Invoke context
        }

    let private admit (configuration: WebConfiguration) (context: HttpContext) =
        task {
            let budget = context.RequestServices.GetRequiredService<AuthenticationAdmission>()
            use lease = budget.Acquire(context)

            if not lease.IsAcquired then
                return Error GateFailure.AdmissionRejected
            else
                match configuration.Oidc with
                | None -> return Error GateFailure.SessionRejected
                | Some oidc ->
                    return!
                        RequestGate.actorPost
                            oidc
                            configuration.Binding
                            configuration.RequestGate.MaximumJsonBytes
                            (context.RequestServices.GetRequiredService<IAntiforgery>())
                            context
        }

    let private workload endpoint =
        if Api.identity endpoint = EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Status) then
            RequestWorkload.Recovery
        elif Api.effect endpoint = EndpointEffect.Read then
            RequestWorkload.Read
        else
            RequestWorkload.Write

    let private input
        (handler: ClaimCore.Web.Route)
        (configuration: WebConfiguration)
        (context: HttpContext)
        =
        task {
            match! HttpBody.readRequest configuration.RequestGate.MaximumJsonBytes context with
            | Error problem -> return Error(RouteSupport.inputFailure context problem)
            | Ok bytes ->
                match StrictJson.read configuration.RequestGate.MaximumJsonBytes bytes with
                | Error _ ->
                    return Error(RouteSupport.hostFailure context HostFailureReason.InputInvalid)
                | Ok value ->
                    match handler.Prepare value with
                    | Error _ ->
                        return
                            Error(RouteSupport.hostFailure context HostFailureReason.InputInvalid)
                    | Ok _ when context.RequestAborted.IsCancellationRequested ->
                        return Error(RouteSupport.inputFailure context BodyProblem.BodyCancelled)
                    | Ok prepared -> return Ok prepared
        }

    let private installationBinding (context: HttpContext) =
        match context.Request.Headers.TryGetValue "ClaimCore-Installation" with
        | true, values when values.Count = 1 ->
            Codec.uuid.Read "$.installation" (JsonValue.String(string values[0]))
            |> Result.toOption

        | _ -> None

    let handle handler configuration withActor (context: HttpContext) =
        let endpoint = handler.Description

        task {
            HttpHeaders.noStore context

            if not (handshake context) then
                return RouteSupport.hostFailure context HostFailureReason.ContractMismatch
            else
                match! admit configuration context with
                | Error reason -> return RouteSupport.gateFailure context reason
                | Ok principal ->
                    let admission = context.RequestServices.GetRequiredService<PrincipalAdmission>()

                    let! lease =
                        admission.Acquire(
                            AuthenticatedPrincipals.key principal,
                            workload endpoint,
                            context.RequestAborted
                        )

                    match lease with
                    | None -> return RouteSupport.hostFailure context HostFailureReason.RateLimited
                    | Some lease ->
                        // DI releases this after result writing, not at the Task<IResult> boundary.
                        try
                            context.RequestServices.GetRequiredService<RequestCapacity>().Own lease
                        with error ->
                            lease.Dispose()
                            raise error

                        match! input handler configuration context with
                        | Error result -> return result
                        | Ok prepared ->
                            prepared.OperationId
                            |> Option.iter (fun id -> RequestLog.operation id context)

                            RouteSupport.markDispatched context

                            let cancellation =
                                if Api.effect endpoint <> EndpointEffect.Read then
                                    System.Threading.CancellationToken.None
                                else
                                    context.RequestAborted

                            let binding = installationBinding context

                            if Api.requiresInstallation endpoint && binding.IsNone then
                                RouteSupport.markCompleted context

                                return
                                    RouteSupport.hostFailure context HostFailureReason.InputInvalid
                            else
                                let! result =
                                    withActor (AuthenticatedPrincipals.key principal) (fun core ->
                                        task {
                                            if
                                                Api.requiresInstallation endpoint
                                                && not (
                                                    binding
                                                    |> Option.exists (
                                                        Service.matchesInstallation core
                                                    )
                                                )
                                            then
                                                return Error()
                                            else
                                                let! reply =
                                                    prepared.Invoke
                                                        (Some core)
                                                        cancellation
                                                        (fun () ->
                                                            RouteSupport.markCompleted context)

                                                return Ok reply
                                        })

                                match result with
                                | Some(Error()) ->
                                    RouteSupport.markCompleted context

                                    return
                                        RouteSupport.hostFailure
                                            context
                                            HostFailureReason.InstallationMismatch
                                | Some(Ok reply) -> return RouteSupport.reply reply context
                                | None ->
                                    RouteSupport.markCompleted context
                                    let! reply = prepared.Invoke None cancellation ignore
                                    return RouteSupport.reply reply context
        }
