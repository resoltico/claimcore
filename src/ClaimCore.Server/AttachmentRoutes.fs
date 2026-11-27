namespace ClaimCore.Server

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Server.Composition
open ClaimCore.Wire

module internal AttachmentRoutes =
    let private reply classification wire =
        {
            RouteReply.Response = RouteBody.Json { Class = classification; Wire = wire }
            RecordedBy = None
            ExpireBrowserSession = false
        }

    let private failure value =
        let classification =
            match value with
            | AttachmentTransferFailure.Rejected _ -> ReplyKind.Rejected
            | AttachmentTransferFailure.Failed _ -> ReplyKind.Failed

        reply classification (AttachmentTransport.failure.Write value)

    let private uploadReply selection (outcome: ExecuteReply) =
        let represented =
            match selection with
            | Representation.Human -> RepresentationReply.Human(HumanReplies.execute outcome)
            | Representation.Data -> RepresentationReply.Data outcome

        {
            RouteReply.Response = RouteBody.Json(Endpoint.reply Api.operationExecute represented)
            RecordedBy = ApiRoutes.executionActor outcome
            ExpireBrowserSession = false
        }

    let private framingFailure (token: CancellationToken) =
        function
        | BodyProblem.BodyCancelled when StorageFailureLog.clientDisconnected () ->
            raise (OperationCanceledException token)
        | problem ->
            match problem with
            | BodyProblem.BodyTooLarge -> HostFailureReason.BodyTooLarge
            | BodyProblem.BodyUnreadable
            | BodyProblem.BodyCancelled -> HostFailureReason.InputInvalid

    let private uploadLimit maximum (context: HttpContext) =
        match context.Features.Get<IHttpMaxRequestBodySizeFeature>() |> Option.ofObj with
        | Some feature when not feature.IsReadOnly ->
            feature.MaxRequestBodySize <- Nullable(int64 maximum)
        | Some _ -> invalidOp "Upload body limits must be set before the request body is read."
        | None -> ()

    let private withWorkload workload principal (context: HttpContext) token invoke =
        task {
            let admission = context.RequestServices.GetRequiredService<PrincipalAdmission>()
            let! permit = admission.Acquire(principal, workload, token)

            match permit with
            | None -> return Error HostFailureReason.RateLimited
            | Some permit ->
                use _permit = permit
                token.ThrowIfCancellationRequested()
                RouteSupport.markDispatched context
                let! outcome = invoke ()
                RouteSupport.markCompleted context
                return Ok outcome
        }

    let private completeUpload
        boundary
        selection
        manifestLimit
        principal
        (context: HttpContext)
        services
        (token: CancellationToken)
        =
        task {
            uploadLimit (AttachmentTransport.maximumUploadBytes manifestLimit) context

            match!
                AttachmentMultipart.read
                    boundary
                    manifestLimit
                    Service.attachmentUploadIntent
                    context.Request.Body
                    token
            with
            | Error problem -> return Error(framingFailure token problem)
            | Ok(submission, bytes) ->
                RequestLog.operation submission.OperationId context
                let admission = context.RequestServices.GetRequiredService<PrincipalAdmission>()

                let acquire token =
                    task {
                        let! permit = admission.Acquire(principal, RequestWorkload.Write, token)

                        match permit with
                        | None -> return raise (WorkloadAdmissionRejectedException())
                        | Some permit -> return permit
                    }

                let services = Service.withWriteAdmission acquire services
                token.ThrowIfCancellationRequested()
                RouteSupport.markDispatched context

                try
                    let! outcome = Service.executeAttachmentUpload services submission bytes token
                    RouteSupport.markCompleted context
                    return Ok(uploadReply selection outcome)
                with :? WorkloadAdmissionRejectedException ->
                    return Error HostFailureReason.RateLimited
        }

    let private completeRead
        transfer
        manifestLimit
        installation
        principal
        (context: HttpContext)
        services
        token
        =
        task {
            match! HttpBody.readWithin manifestLimit context.Request.Body token with
            | Error problem -> return Error(framingFailure token problem)
            | Ok bytes ->
                match Codec.decode manifestLimit AttachmentTransport.request bytes with
                | Error _ -> return Error HostFailureReason.InputInvalid
                | Ok request ->
                    let action =
                        match transfer with
                        | AttachmentTransfer.Download -> Service.downloadAttachment
                        | AttachmentTransfer.Preview -> Service.previewAttachment
                        | AttachmentTransfer.Upload ->
                            invalidArg (nameof transfer) "Upload uses a manifest."

                    let! outcome =
                        withWorkload RequestWorkload.Read principal context token (fun () ->
                            action services request token)

                    return
                        outcome
                        |> Result.map (function
                            | Outcome.Succeeded content ->
                                {
                                    RouteReply.Response =
                                        RouteBody.Attachment(transfer, content, installation)
                                    RecordedBy = None
                                    ExpireBrowserSession = false
                                }
                            | Outcome.Rejected refusal ->
                                failure (
                                    AttachmentTransferFailure.Rejected(
                                        HumanReplies.rejection refusal
                                    )
                                )
                            | Outcome.Failed fault ->
                                failure (AttachmentTransferFailure.Failed fault))
        }

    let handle transfer (configuration: WebConfiguration) withActor (context: HttpContext) : Task =
        task {
            HttpHeaders.noStore context

            let manifestLimit = configuration.RequestGate.MaximumJsonBytes
            let upload = transfer = AttachmentTransfer.Upload

            let send (result: IResult) = result.ExecuteAsync context

            let gate =
                if upload then
                    ApiRoutes.admitPost
                        (RequestGate.actorUpload (fun context ->
                            (AttachmentMultipart.boundary context).IsSome
                            && AttachmentMultipart.acceptsIdentity context))
                        (AttachmentTransport.maximumUploadBytes manifestLimit)
                        configuration
                else
                    ApiRoutes.admit configuration

            match ApiRoutes.representation context, ApiRoutes.installationBinding context with
            | _ when
                Option.ofObj context.Request.Path.Value
                <> Some(AttachmentTransport.path transfer)
                ->
                do! send (RouteSupport.hostFailure context HostFailureReason.EndpointMissing)
            | _ when context.Request.Method <> "POST" ->
                do! send (RouteSupport.hostFailure context HostFailureReason.MethodRejected)
            | _ when not (ApiRoutes.handshake context) ->
                do! send (RouteSupport.hostFailure context HostFailureReason.ContractMismatch)
            | Error(), _
            | _, None -> do! send (RouteSupport.hostFailure context HostFailureReason.InputInvalid)
            | Ok representation, Some installation ->
                match! gate context with
                | Error problem -> do! send (RouteSupport.gateFailure context problem)
                | Ok principal ->
                    let identity = AuthenticatedPrincipals.key principal

                    let transferAdmission =
                        context.RequestServices.GetRequiredService<AttachmentTransferAdmission>()

                    match transferAdmission.TryAcquire identity with
                    | None ->
                        do! send (RouteSupport.hostFailure context HostFailureReason.RateLimited)
                    | Some lease ->
                        use _lease = lease

                        let stopping =
                            context.RequestServices
                                .GetRequiredService<IHostApplicationLifetime>()
                                .ApplicationStopping

                        use deadline =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                context.RequestAborted,
                                stopping
                            )

                        deadline.CancelAfter AdmissionPolicy.attachmentTransferDeadline

                        try
                            let! outcome =
                                withActor identity deadline.Token (fun core ->
                                    task {
                                        if not (Service.matchesInstallation core installation) then
                                            return Error HostFailureReason.InstallationMismatch
                                        else
                                            let core =
                                                Service.withClaims
                                                    (AuthenticatedPrincipals.claims principal)
                                                    core

                                            match transfer with
                                            | AttachmentTransfer.Upload ->
                                                let boundary =
                                                    AttachmentMultipart.boundary context
                                                    |> Option.get

                                                return!
                                                    completeUpload
                                                        boundary
                                                        representation
                                                        manifestLimit
                                                        identity
                                                        context
                                                        core
                                                        deadline.Token
                                            | AttachmentTransfer.Download
                                            | AttachmentTransfer.Preview ->
                                                let core =
                                                    Service.withReadCancellation
                                                        context.RequestAborted
                                                        core

                                                return!
                                                    completeRead
                                                        transfer
                                                        manifestLimit
                                                        installation
                                                        identity
                                                        context
                                                        core
                                                        deadline.Token
                                    })

                            if not (StorageFailureLog.clientDisconnected ()) then
                                deadline.Token.ThrowIfCancellationRequested()

                            // The body publisher is still owned by the transfer lease when the deadline fires.
                            use _abort = deadline.Token.Register(fun () -> context.Abort())

                            match outcome with
                            | Some(Error reason) ->
                                do! send (RouteSupport.hostFailure context reason)
                            | Some(Ok result) ->
                                context.Response.Headers["ClaimCore-Installation"] <-
                                    installation.ToString("D")

                                do! send (RouteSupport.actorReply result context)
                            | None ->
                                do!
                                    send (
                                        RouteSupport.hostFailure
                                            context
                                            HostFailureReason.BeforeDispatchFailed
                                    )

                            if not (StorageFailureLog.clientDisconnected ()) then
                                do! context.Response.CompleteAsync()
                        with :? OperationCanceledException as error when
                            error.CancellationToken = deadline.Token
                            && deadline.IsCancellationRequested ->
                            if StorageFailureLog.clientDisconnected () then
                                context.RequestAborted.ThrowIfCancellationRequested()
                            else
                                context.Abort()
        }
