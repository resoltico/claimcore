namespace ClaimCore.Server

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ClaimCore.Application
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.Server.Composition

module internal ExportRoutes =
    let processingBudget = TimeSpan.FromSeconds 15.

    let private failure context value =
        RequestLog.reply
            (match value with
             | ExportFailure.Rejected _ -> ClaimCore.Application.Contracts.ReplyKind.Rejected
             | _ -> ClaimCore.Application.Contracts.ReplyKind.Failed)
            context

        ExportTransport.failure.Write value |> WebWire.reply

    let private failureReply value =
        {
            RouteReply.Response =
                RouteBody.Json
                    {
                        Class =
                            (match value with
                             | ExportFailure.Rejected _ ->
                                 ClaimCore.Application.Contracts.ReplyKind.Rejected
                             | _ -> ClaimCore.Application.Contracts.ReplyKind.Failed)
                        Wire = ExportTransport.failure.Write value
                    }
            RecordedBy = None
            ExpireBrowserSession = false
        }

    let private selection configuration (context: HttpContext) =
        task {
            match! HttpBody.readRequest configuration.RequestGate.MaximumJsonBytes context with
            | Error problem -> return Error(RouteSupport.inputFailure context problem)
            | Ok bytes ->
                match
                    Codec.decode
                        configuration.RequestGate.MaximumJsonBytes
                        ExportTransport.request
                        bytes
                with
                | Error _ ->
                    return Error(RouteSupport.hostFailure context HostFailureReason.InputInvalid)
                | Ok value -> return Ok value
        }

    let handle configuration withActor (context: HttpContext) =
        task {
            HttpHeaders.noStore context

            let representation =
                match context.Request.Headers.TryGetValue Representation.HeaderName with
                | true, values -> Some(values |> Seq.map string |> Seq.toList)
                | _ -> None
                |> Representation.select

            if Option.ofObj context.Request.Path.Value <> Some ExportTransport.path then
                return RouteSupport.hostFailure context HostFailureReason.EndpointMissing
            elif context.Request.Method <> "POST" then
                return RouteSupport.hostFailure context HostFailureReason.MethodRejected
            elif not (ApiRoutes.handshake context) then
                return RouteSupport.hostFailure context HostFailureReason.ContractMismatch
            elif Result.isError representation then
                return RouteSupport.hostFailure context HostFailureReason.InputInvalid
            else
                match! ApiRoutes.admit configuration context with
                | Error reason -> return RouteSupport.gateFailure context reason
                | Ok principal ->
                    let admission = context.RequestServices.GetRequiredService<PrincipalAdmission>()

                    let! lease =
                        admission.Acquire(
                            AuthenticatedPrincipals.key principal,
                            RequestWorkload.Read,
                            context.RequestAborted
                        )

                    match lease with
                    | None -> return RouteSupport.hostFailure context HostFailureReason.RateLimited
                    | Some lease ->
                        try
                            context.RequestServices.GetRequiredService<RequestCapacity>().Own lease
                        with error ->
                            lease.Dispose()
                            raise error

                        match! selection configuration context with
                        | Error result -> return result
                        | Ok selection ->
                            match ApiRoutes.installationBinding context with
                            | None ->
                                return
                                    RouteSupport.hostFailure context HostFailureReason.InputInvalid
                            | Some installation ->
                                let stopping =
                                    context.RequestServices
                                        .GetRequiredService<IHostApplicationLifetime>()
                                        .ApplicationStopping

                                use budget =
                                    CancellationTokenSource.CreateLinkedTokenSource(
                                        context.RequestAborted,
                                        stopping
                                    )

                                budget.CancelAfter(processingBudget)
                                RouteSupport.markDispatched context

                                let! result =
                                    withActor
                                        (AuthenticatedPrincipals.key principal)
                                        (fun services ->
                                            task {
                                                if
                                                    not (
                                                        Service.matchesInstallation
                                                            services
                                                            installation
                                                    )
                                                then
                                                    return
                                                        Error
                                                            HostFailureReason.InstallationMismatch
                                                else
                                                    let services =
                                                        Service.withClaims
                                                            (AuthenticatedPrincipals.claims
                                                                principal)
                                                            services

                                                    let! result =
                                                        task {
                                                            try
                                                                return!
                                                                    CsvExport.materialize
                                                                        Limits.MaximumResponseBytes
                                                                        budget.Token
                                                                        (CsvExport.traversal
                                                                            services
                                                                            selection)
                                                            with :? OperationCanceledException ->
                                                                return
                                                                    Error(
                                                                        ExportFailure
                                                                            .ProcessingTimeout
                                                                            ExportAdvice
                                                                                .processingTimeout
                                                                    )
                                                        }

                                                    RouteSupport.markCompleted context

                                                    match result with
                                                    | Error error -> return Ok(failureReply error)
                                                    | Ok bytes ->
                                                        return
                                                            Ok
                                                                {
                                                                    RouteReply.Response =
                                                                        RouteBody.Csv(
                                                                            bytes,
                                                                            ExportTransport
                                                                                .filename
                                                                                selection,
                                                                            installation
                                                                        )
                                                                    RecordedBy = None
                                                                    ExpireBrowserSession = false
                                                                }
                                            })

                                match result with
                                | None ->
                                    return
                                        failure
                                            context
                                            (ExportFailure.Failed
                                                ClaimCore.Application.Contracts
                                                    .CoreFault.StoreUnavailable)
                                | Some(Error reason) ->
                                    return RouteSupport.hostFailure context reason
                                | Some(Ok reply) -> return RouteSupport.actorReply reply context
        }
