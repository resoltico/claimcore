namespace ClaimCore.Server

open System
open System.Globalization
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ClaimCore.Application
open ClaimCore.Application.Contracts
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

    let private pageWindow (context: HttpContext) =
        let header name =
            match context.Request.Headers.TryGetValue name with
            | false, _ -> Ok None
            | true, values when values.Count = 1 ->
                match Option.ofObj values[0] with
                | Some text -> Ok(Some text)
                | None -> Error()
            | _ -> Error()

        match header ExportTransport.pageSizeHeader, header ExportTransport.afterHeader with
        | Ok None, Ok None -> Ok None
        | Ok(Some size), Ok after ->
            match Int32.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, count when
                count >= 1
                && count <= Paging.MaximumPageSize
                && (after
                    |> Option.forall (fun cursor ->
                        cursor.Length > 0
                        && cursor.Length <= ExportTransport.maximumContinuationCharacters))
                ->
                Ok(Some { Limit = count; After = after })
            | _ -> Error()
        | _ -> Error()

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
            elif Result.isError representation || Result.isError (pageWindow context) then
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
                            let window = pageWindow context |> Result.defaultValue None

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
                                        budget.Token
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
                                                        AuthenticatedPrincipals.serviceContext
                                                            principal
                                                            services
                                                        |> Service.withReadCancellation
                                                            budget.Token

                                                    let continuation =
                                                        window
                                                        |> Option.map (fun page ->
                                                            let digest =
                                                                ExportTransport.request.Write
                                                                    selection
                                                                |> CanonicalWriter.bytes
                                                                |> System.Security.Cryptography
                                                                    .SHA256.HashData
                                                                |> Convert.ToHexStringLower

                                                            {
                                                                ExportContinuationRequest.SelectionDigest =
                                                                    digest
                                                                    + ":"
                                                                    + page.Limit.ToString(
                                                                        CultureInfo
                                                                            .InvariantCulture
                                                                    )
                                                                After = page.After
                                                            })

                                                    let materialize snapshot after seal =
                                                        task {
                                                            let page =
                                                                window
                                                                |> Option.map (fun value ->
                                                                    { value with After = after })

                                                            let limit =
                                                                window
                                                                |> Option.map _.Limit
                                                                |> Option.defaultValue
                                                                    Paging.MaximumPageSize

                                                            let! prepared =
                                                                CsvExport.materializeWindow
                                                                    Limits.MaximumResponseBytes
                                                                    page
                                                                    budget.Token
                                                                    (CsvExport.traversalAt
                                                                        limit
                                                                        snapshot
                                                                        selection)

                                                            return
                                                                prepared
                                                                |> Result.map (fun document ->
                                                                    {
                                                                        ExportCandidate.Body =
                                                                            {
                                                                                Bytes =
                                                                                    document.Bytes
                                                                                NextCursor =
                                                                                    Option.map
                                                                                        seal
                                                                                        document
                                                                                            .NextCursor
                                                                            }
                                                                        RowCount =
                                                                            document.RowCount
                                                                        Manifest =
                                                                            document.Manifest
                                                                        CaseId = document.CaseId
                                                                    })
                                                        }

                                                    let! document =
                                                        task {
                                                            try
                                                                match!
                                                                    Service.export
                                                                        services
                                                                        selection
                                                                        continuation
                                                                        budget.Token
                                                                        materialize
                                                                with
                                                                | Outcome.Succeeded result ->
                                                                    return result
                                                                | Outcome.Rejected refusal ->
                                                                    return
                                                                        Error(
                                                                            ExportFailure.Rejected(
                                                                                HumanReplies
                                                                                    .rejection
                                                                                    refusal
                                                                            )
                                                                        )
                                                                | Outcome.Failed fault ->
                                                                    return
                                                                        Error(
                                                                            ExportFailure.Failed
                                                                                fault
                                                                        )
                                                            with
                                                            | :? OperationCanceledException when
                                                                StorageFailureLog
                                                                    .clientDisconnected ()
                                                                ->
                                                                return
                                                                    raise (
                                                                        OperationCanceledException(
                                                                            context.RequestAborted
                                                                        )
                                                                    )
                                                            | :? OperationCanceledException ->
                                                                return
                                                                    Error(
                                                                        ExportFailure
                                                                            .ProcessingTimeout
                                                                            ExportAdvice
                                                                                .processingTimeout
                                                                    )
                                                        }

                                                    RouteSupport.markCompleted context

                                                    return
                                                        match document with
                                                        | Error failure -> Ok(failureReply failure)
                                                        | Ok document ->
                                                            Ok
                                                                {
                                                                    RouteReply.Response =
                                                                        RouteBody.Csv(
                                                                            document,
                                                                            ExportTransport
                                                                                .filename
                                                                                selection,
                                                                            installation
                                                                        )
                                                                    RecordedBy = None
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
