namespace ClaimCore.Web

open ClaimCore.Wire
open ClaimCore.Presentation
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
    let route =
        function
        | ActorEndpoint.Query QueryTaskId.CasePayments ->
            Route.representedRead Api.casePayments HumanReadDocuments.payments Service.payments
        | ActorEndpoint.Query QueryTaskId.CaseNotes ->
            Route.representedRead Api.caseNotes HumanReadDocuments.notes Service.notes
        | ActorEndpoint.Query QueryTaskId.ReminderGet ->
            Route.representedRead Api.reminder HumanReadDocuments.reminder Service.reminder
        | ActorEndpoint.Query QueryTaskId.ReminderList ->
            Route.representedRead Api.reminders HumanReadDocuments.reminders Service.reminders
        | ActorEndpoint.Query QueryTaskId.CaseTimeline ->
            Route.representedRead Api.caseTimeline HumanReadDocuments.timeline Service.timeline
        | ActorEndpoint.Query QueryTaskId.Context ->
            Route.representedRead Api.context HumanReadDocuments.context Service.context
        | ActorEndpoint.Query QueryTaskId.CaseGet ->
            Route.representedRead Api.caseGet HumanCaseDocument.ofCore Service.get
        | ActorEndpoint.Query QueryTaskId.CaseAssignees ->
            Route.representedRead Api.caseAssignees HumanReadDocuments.assignees Service.assignees
        | ActorEndpoint.Query QueryTaskId.CaseList ->
            Route.representedRead Api.caseList HumanReadDocuments.cases Service.list
        | ActorEndpoint.Query QueryTaskId.CaseSummary ->
            Route.representedRead Api.caseSummary HumanReadDocuments.summary Service.caseSummary
        | ActorEndpoint.Query QueryTaskId.WorkViews ->
            Route.representedRead Api.workViews HumanReadDocuments.views Service.workViews
        | ActorEndpoint.Query QueryTaskId.OperationGet ->
            Route.represented
                Api.operationGet
                Api.operationGetUnavailable
                (fun _ reply -> HumanReplies.lookup reply)
                (fun _ -> None)
                Service.operationGet
        | ActorEndpoint.Query QueryTaskId.PeopleList ->
            Route.representedRead Api.peopleList HumanReadDocuments.actors Service.actors
        | ActorEndpoint.Query QueryTaskId.PeopleGrants ->
            Route.representedRead Api.peopleGrants HumanReadDocuments.grants Service.grants
        | ActorEndpoint.Query QueryTaskId.PeopleHistory ->
            Route.representedRead Api.peopleHistory HumanReadDocuments.history Service.peopleHistory
        | ActorEndpoint.Query QueryTaskId.PeopleRequests ->
            Route.representedRead
                Api.peopleRequests
                HumanReadDocuments.requests
                Service.accessRequests
        | ActorEndpoint.Command CommandTaskId.PeopleRequestsDismiss ->
            Route.representedRead
                Api.peopleRequestsDismiss
                HumanReadDocuments.dismissed
                Service.dismissAccessRequest
        | ActorEndpoint.Operation OperationEndpoint.Preview ->
            Route.represented
                Api.operationPreview
                Api.operationPreviewUnavailable
                HumanReplies.preview
                (fun _ -> None)
                Service.preview
        | ActorEndpoint.Operation OperationEndpoint.Execute ->
            Route.represented
                Api.operationExecute
                Api.operationExecuteUnavailable
                (fun _ reply -> HumanReplies.execute reply)
                (fun reply ->
                    match reply.Outcome with
                    | ExecuteResult.Accepted receipt -> Some receipt.RecordedBy.ActorId
                    | _ -> None)
                Service.execute

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
        if Api.effect endpoint = EndpointEffect.Read then
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

    let private confirmationStore (admission: PrincipalAdmission) principal (store: IStore) =
        { new IStore with
            member _.Context query = store.Context query
            member _.Read operation = store.Read operation

            member _.Write operation =
                task {
                    let! acquired =
                        admission.Acquire(
                            principal,
                            RequestWorkload.Write,
                            operation.CancellationToken
                        )

                    match acquired with
                    | None -> return raise (WorkloadAdmissionRejectedException())
                    | Some lease ->
                        use _owned = lease
                        return! store.Write operation
                }
        }

    let handle (handler: ClaimCore.Web.Route) configuration withActor (context: HttpContext) =
        let endpoint = handler.Description

        task {
            HttpHeaders.noStore context

            let selection =
                match context.Request.Headers.TryGetValue Representation.HeaderName with
                | true, values -> Some(values |> Seq.map string |> Seq.toList)
                | _ -> None
                |> Representation.select

            if not (handshake context) then
                return RouteSupport.hostFailure context HostFailureReason.ContractMismatch
            elif Result.isError selection then
                return RouteSupport.hostFailure context HostFailureReason.InputInvalid
            else
                let representation =
                    match selection with
                    | Ok value -> value
                    | Error() -> invalidOp "Representation must be admitted before dispatch."

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
                                        let core =
                                            Service.withClaims
                                                (AuthenticatedPrincipals.claims principal)
                                                core

                                        let core =
                                            match endpoint.Id with
                                            | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.OperationGet) ->
                                                Service.mapStore
                                                    (confirmationStore
                                                        admission
                                                        (AuthenticatedPrincipals.key principal))
                                                    core
                                            | _ -> core

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
                                                return Error HostFailureReason.InstallationMismatch
                                            else
                                                try
                                                    let! reply =
                                                        prepared.Invoke
                                                            (Result.defaultValue
                                                                Representation.Data
                                                                selection)
                                                            (Some core)
                                                            cancellation
                                                            (fun () ->
                                                                RouteSupport.markCompleted context)

                                                    return Ok reply
                                                with :? WorkloadAdmissionRejectedException ->
                                                    return Error HostFailureReason.RateLimited
                                        })

                                match result with
                                | Some(Error reason) ->
                                    RouteSupport.markCompleted context
                                    return RouteSupport.hostFailure context reason
                                | Some(Ok reply) -> return RouteSupport.actorReply reply context
                                | None ->
                                    RouteSupport.markCompleted context

                                    let! reply =
                                        prepared.Invoke representation None cancellation ignore

                                    return RouteSupport.actorReply reply context
        }
