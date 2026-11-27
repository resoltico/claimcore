namespace ClaimCore.Server

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
open ClaimCore.Server.Composition

module internal ApiRoutes =
    let internal executionActor (reply: ExecuteReply) =
        match reply.Outcome with
        | ExecuteResult.Accepted receipt ->
            receipt.RecordedBy |> AuthorityRef.actor |> Option.map _.ActorId
        | _ -> None

    let internal representation (context: HttpContext) =
        match context.Request.Headers.TryGetValue Representation.HeaderName with
        | true, values -> Some(values |> Seq.map string |> Seq.toList)
        | _ -> None
        |> Representation.select

    // This total endpoint-to-handler table has no nested decisions; keeping it exhaustive makes missing transport bindings compiler errors.
    let route =
        function
        | ActorEndpoint.Query QueryTaskId.CasePayments ->
            Route.representedRead Api.casePayments PaymentsScreen.document Service.payments
        | ActorEndpoint.Query QueryTaskId.CaseNotes ->
            Route.representedRead Api.caseNotes NotesScreen.document Service.notes
        | ActorEndpoint.Query QueryTaskId.CaseAttachments ->
            Route.representedRead
                Api.caseAttachments
                AttachmentsScreen.document
                (fun services (request: CaseGetRequest) token ->
                    Service.caseAttachments services request.Target token)
        | ActorEndpoint.Query QueryTaskId.ReminderGet ->
            Route.representedRead Api.reminder ReminderScreen.document Service.reminder
        | ActorEndpoint.Query QueryTaskId.ReminderList ->
            Route.representedReadWithInput
                Api.reminders
                ReminderListScreen.document
                Service.reminders
        | ActorEndpoint.Query QueryTaskId.CaseTimeline ->
            Route.representedRead Api.caseTimeline TimelineScreen.document Service.timeline
        | ActorEndpoint.Query QueryTaskId.Context ->
            Route.representedRead Api.context ContextScreen.document Service.context
        | ActorEndpoint.Query QueryTaskId.CaseGet ->
            Route.representedRead Api.caseGet HumanCaseDocument.ofCore Service.get
        | ActorEndpoint.Query QueryTaskId.CaseParties ->
            Route.representedRead Api.caseParties PartiesScreen.document Service.parties
        | ActorEndpoint.Query QueryTaskId.CaseAssignees ->
            Route.representedRead Api.caseAssignees AssigneesScreen.document Service.assignees
        | ActorEndpoint.Query QueryTaskId.CaseList ->
            Route.representedRead Api.caseList CaseListScreen.document Service.list
        | ActorEndpoint.Query QueryTaskId.CaseSummary ->
            Route.representedRead Api.caseSummary CaseTotalsScreen.document Service.caseSummary
        | ActorEndpoint.Query QueryTaskId.WorkViews ->
            Route.representedRead Api.workViews WorkViewsScreen.document Service.workViews
        | ActorEndpoint.Query QueryTaskId.OperationGet ->
            Route.represented
                Api.operationGet
                Api.operationGetUnavailable
                (fun _ reply -> HumanReplies.lookup reply)
                (fun _ -> None)
                Service.operationGet
        | ActorEndpoint.Query QueryTaskId.PeopleList ->
            Route.representedRead Api.peopleList PeopleScreen.document Service.actors
        | ActorEndpoint.Query QueryTaskId.PeopleGrants ->
            Route.representedRead Api.peopleGrants PersonScreen.document Service.grants
        | ActorEndpoint.Query QueryTaskId.PeopleHistory ->
            Route.representedRead
                Api.peopleHistory
                AccessHistoryScreen.document
                Service.peopleHistory
        | ActorEndpoint.Query QueryTaskId.PeopleRequests ->
            Route.representedRead
                Api.peopleRequests
                AccessRequestsScreen.document
                Service.accessRequests
        | ActorEndpoint.Query QueryTaskId.DisclosureTrail ->
            Route.representedRead
                Api.disclosureTrail
                DisclosureTrailScreen.document
                Service.disclosureTrail
        | ActorEndpoint.Command CommandTaskId.RequestAccess ->
            Route.representedRead
                Api.peopleRequestsSubmit
                AccessRequestsScreen.submittedDocument
                Service.requestAccess
        | ActorEndpoint.Command CommandTaskId.DismissAccessRequest ->
            Route.representedRead
                Api.peopleRequestsDismiss
                AccessRequestsScreen.dismissedDocument
                Service.dismissAccessRequest
        | ActorEndpoint.Command CommandTaskId.TerminateOwnSessions ->
            Route.represented
                Api.terminateOwnSessions
                (fun _ -> SessionTerminationResult.Failed CoreFault.StoreUnavailable)
                (fun _ -> HumanReplies.sessionTermination)
                (fun _ -> None)
                Service.terminateOwnSessions
        | ActorEndpoint.Command CommandTaskId.TerminatePersonSessions ->
            Route.represented
                Api.terminatePersonSessions
                (fun _ -> SessionTerminationResult.Failed CoreFault.StoreUnavailable)
                (fun _ -> HumanReplies.sessionTermination)
                (fun _ -> None)
                Service.terminatePersonSessions
        | ActorEndpoint.Operation OperationEndpoint.Preview ->
            Route.represented
                Api.operationPreview
                Api.operationPreviewUnavailable
                HumanReplies.previewSubmission
                (fun _ -> None)
                Service.previewSubmission
        | ActorEndpoint.Operation OperationEndpoint.Execute ->
            Route.represented
                Api.operationExecute
                Api.operationExecuteUnavailable
                (fun _ reply -> HumanReplies.execute reply)
                executionActor
                Service.executeSubmission

    let handshake (context: HttpContext) =
        match context.Request.Headers.TryGetValue "ClaimCore-Contract" with
        | true, values when values.Count = 1 -> string values[0] = ContractIdentity.value
        | _ -> false

    let ingress (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            if
                (Option.ofObj context.Request.Path.Value
                 |> Option.exists (fun path ->
                     path = ExportTransport.path || (AttachmentTransport.tryPath path).IsSome))
                && context.Request.Method <> "POST"
            then
                do!
                    (RouteSupport.hostFailure context HostFailureReason.MethodRejected).ExecuteAsync
                        context
            else
                match
                    Api.all
                    |> List.tryFind (fun endpoint ->
                        Some(Api.path endpoint) = Option.ofObj context.Request.Path.Value)
                with
                | Some endpoint when context.Request.Method <> (Api.method endpoint).Method ->
                    do!
                        (RouteSupport.hostFailure context HostFailureReason.MethodRejected)
                            .ExecuteAsync
                            context
                | _ -> do! next.Invoke context
        }

    let internal admitPost
        verify
        maximumBytes
        (configuration: WebConfiguration)
        (context: HttpContext)
        =
        task {
            let budget = context.RequestServices.GetRequiredService<AuthenticationAdmission>()

            let! admitted =
                budget.Run(
                    context,
                    fun _ ->
                        task {
                            match configuration.Oidc with
                            | None -> return Error GateFailure.SessionRejected
                            | Some oidc ->
                                return!
                                    verify
                                        oidc
                                        configuration.Binding
                                        maximumBytes
                                        (context.RequestServices.GetRequiredService<IAntiforgery>())
                                        context
                        }
                )

            return admitted |> Option.defaultValue (Error GateFailure.AdmissionRejected)
        }

    let internal admit (configuration: WebConfiguration) context =
        admitPost
            RequestGate.actorPost
            configuration.RequestGate.MaximumJsonBytes
            configuration
            context

    let private workload endpoint =
        if Api.effect endpoint = EndpointEffect.Read then
            RequestWorkload.Read
        else
            RequestWorkload.Write

    let private input
        (handler: ClaimCore.Server.Route)
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

    let internal installationBinding (context: HttpContext) =
        match context.Request.Headers.TryGetValue "ClaimCore-Installation" with
        | true, values when values.Count = 1 ->
            Codec.uuid.Read "$.installation" (JsonValue.String(string values[0]))
            |> Result.toOption
            |> Option.filter ((<>) System.Guid.Empty)

        | _ -> None

    let private phaseAdmission (admission: PrincipalAdmission) principal workload token =
        task {
            let! acquired = admission.Acquire(principal, workload, token)

            match acquired with
            | None -> return raise (WorkloadAdmissionRejectedException())
            | Some lease -> return lease
        }

    let handle (handler: ClaimCore.Server.Route) configuration withActor (context: HttpContext) =
        let endpoint = handler.Description

        let phasedLookup =
            endpoint.Id = EndpointId.Actor(ActorEndpoint.Query QueryTaskId.OperationGet)

        task {
            HttpHeaders.noStore context

            let selection = representation context

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

                    let principalKey = AuthenticatedPrincipals.key principal

                    let! lease =
                        if phasedLookup then
                            // Ingress remains held through response writing. A lookup acquires database
                            // capacity only while executing a provider phase.
                            Task.FromResult(Some(AdmissionLease.once ignore))
                        else
                            admission.Acquire(
                                principalKey,
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

                            let suppliedBinding =
                                context.Request.Headers.ContainsKey "ClaimCore-Installation"

                            if
                                (Api.requiresInstallation endpoint || suppliedBinding)
                                && binding.IsNone
                            then
                                RouteSupport.markCompleted context

                                return
                                    RouteSupport.hostFailure context HostFailureReason.InputInvalid
                            else
                                let! result =
                                    withActor
                                        (AuthenticatedPrincipals.key principal)
                                        cancellation
                                        (fun core ->
                                            let core =
                                                AuthenticatedPrincipals.serviceContext
                                                    principal
                                                    core

                                            let core =
                                                if phasedLookup then
                                                    core
                                                    |> Service.withReadAdmission (
                                                        phaseAdmission
                                                            admission
                                                            principalKey
                                                            RequestWorkload.Read
                                                    )
                                                    |> Service.withWriteAdmission (
                                                        phaseAdmission
                                                            admission
                                                            principalKey
                                                            RequestWorkload.Write
                                                    )
                                                else
                                                    core

                                            let core =
                                                if Api.effect endpoint = EndpointEffect.Read then
                                                    Service.withReadCancellation
                                                        context.RequestAborted
                                                        core
                                                else
                                                    core

                                            task {
                                                if
                                                    (Api.requiresInstallation endpoint
                                                     || suppliedBinding)
                                                    && not (
                                                        binding
                                                        |> Option.exists (
                                                            Service.matchesInstallation core
                                                        )
                                                    )
                                                then
                                                    return
                                                        Error
                                                            HostFailureReason.InstallationMismatch
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
                                                                    RouteSupport.markCompleted
                                                                        context)

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
