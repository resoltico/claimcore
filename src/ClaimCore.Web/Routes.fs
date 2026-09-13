namespace ClaimCore.Web

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Application

/// Thin HTTP-v2 endpoint bindings. The host validates transport admission and exact input shapes,
/// then calls one typed core endpoint. It contains no recovery lifecycle or claims business logic.
module Routes =
    let private requestToken (context: HttpContext) = context.RequestAborted
    let private mutationToken = CancellationToken.None

    let private json admit maximumBytes decode invoke project context =
        task {
            match! RouteSupport.admittedBody admit maximumBytes context with
            | Error result -> return result
            | Ok bytes ->
                match decode bytes with
                | Error message -> return RouteSupport.inputFailure context message
                | Ok input ->
                    let! outcome = invoke input
                    return project outcome
        }

    let get admit maximumBytes (runtime: IWebRuntime) context =
        json
            admit
            maximumBytes
            HttpInput.caseReference
            (fun reference -> runtime.Core.Get(reference, requestToken context))
            WebWire.get
            context

    let list admit maximumBytes (runtime: IWebRuntime) context =
        json
            admit
            maximumBytes
            (HttpInput.page (runtime.Core.Describe()).Contract.MaximumPageSize)
            (fun input ->
                runtime.Core.List(
                    {
                        AfterReference = input.Cursor
                        Limit = input.Limit
                    },
                    requestToken context
                ))
            WebWire.list
            context

    let history admit maximumBytes (runtime: IWebRuntime) context =
        json
            admit
            maximumBytes
            (HttpInput.history (runtime.Core.Describe()).Contract.MaximumPageSize)
            (fun input ->
                runtime.Core.History(
                    {
                        CaseReference = input.CaseReference
                        AfterCursor = input.Cursor
                        Limit = input.Limit
                        Detail = input.Detail
                    },
                    requestToken context
                ))
            WebWire.history
            context

    let observe admit maximumBytes (runtime: IWebRuntime) context =
        json
            admit
            maximumBytes
            HttpInput.operationId
            (fun operationId -> runtime.Core.ObserveOperation(operationId, requestToken context))
            WebWire.observe
            context

    let prepare admit maximumBytes (runtime: IWebRuntime) context =
        json
            admit
            maximumBytes
            HttpInput.draft
            (fun draft -> runtime.Core.Prepare(draft, mutationToken))
            (WebWire.prepare "command.prepare")
            context

    let submit admit maximumBytes (runtime: IWebRuntime) context =
        json
            admit
            maximumBytes
            HttpInput.resolve
            (fun input ->
                runtime.Core.Recovery.Resolve(
                    input.OperationId,
                    input.RequestSha256,
                    mutationToken
                ))
            (WebRecoveryWire.resolve "command.execute")
            context

    let recoveryList admit maximumBytes (runtime: IWebRuntime) context =
        json
            admit
            maximumBytes
            (HttpInput.page (runtime.Core.Describe()).Contract.MaximumPageSize)
            (fun input ->
                runtime.Core.Recovery.List(input.Cursor, input.Limit, requestToken context))
            WebRecoveryWire.list
            context

    let recoveryInspect admit maximumBytes (runtime: IWebRuntime) context =
        json
            admit
            maximumBytes
            HttpInput.operationId
            (fun operationId -> runtime.Core.Recovery.Inspect(operationId, requestToken context))
            WebRecoveryWire.inspect
            context

    let recoveryResolve admit maximumBytes (runtime: IWebRuntime) context =
        json
            admit
            maximumBytes
            HttpInput.resolve
            (fun input ->
                runtime.Core.Recovery.Resolve(
                    input.OperationId,
                    input.RequestSha256,
                    mutationToken
                ))
            (WebRecoveryWire.resolve "recovery.resolve")
            context

    let recoveryDismiss admit maximumBytes (runtime: IWebRuntime) context =
        json
            admit
            maximumBytes
            HttpInput.dismiss
            (fun input ->
                runtime.Core.Recovery.Dismiss(
                    input.OperationId,
                    input.RequestSha256,
                    input.Confirmed,
                    mutationToken
                ))
            WebRecoveryWire.dismiss
            context

    let recoveryExport admit maximumBytes (runtime: IWebRuntime) context =
        task {
            match! RouteSupport.admittedBody admit maximumBytes context with
            | Error result -> return result
            | Ok bytes ->
                match HttpInput.resolve bytes with
                | Error message -> return RouteSupport.inputFailure context message
                | Ok input ->
                    match!
                        runtime.Core.Recovery.ExportEnvelope(
                            input.OperationId,
                            input.RequestSha256,
                            requestToken context
                        )
                    with
                    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact) ->
                        let expectedName =
                            "claimcore-recovery-" + input.OperationId.ToString("D") + ".json"

                        if
                            artifact.FileName <> expectedName
                            || artifact.MediaType <> "application/vnd.claimcore.recovery+json"
                        then
                            return
                                RouteSupport.hostFailure
                                    context
                                    StatusCodes.Status500InternalServerError
                                    "WEB_PROTOCOL"
                                    "Recovery export metadata was invalid."
                                    None
                        else
                            return
                                Results.File(artifact.Bytes, artifact.MediaType, artifact.FileName)
                    | other -> return WebRecoveryWire.export other
        }

    let private raw admit maximumBytes invoke project context =
        task {
            match! RouteSupport.admittedBody admit maximumBytes context with
            | Error result -> return result
            | Ok source ->
                let! outcome = invoke source
                return project outcome
        }

    let private retain admit maximumBytes sourceDigestHeader invoke endpoint context =
        task {
            match! RouteSupport.admittedBody admit maximumBytes context with
            | Error result -> return result
            | Ok source ->
                match RouteSupport.sourceDigest sourceDigestHeader context with
                | Error result -> return result
                | Ok digest ->
                    let! outcome = invoke source digest
                    return WebRecoveryWire.importRetain endpoint outcome
        }

    let envelopePreview admit maximumBytes (runtime: IWebRuntime) context =
        raw
            admit
            maximumBytes
            (fun source ->
                runtime.Core.Recovery.PreviewEnvelopeImport(source, requestToken context))
            (WebRecoveryWire.importQuery "recovery.importEnvelopePreview")
            context

    let envelopeRetain admit maximumBytes sourceDigestHeader (runtime: IWebRuntime) context =
        retain
            admit
            maximumBytes
            sourceDigestHeader
            (fun source digest ->
                runtime.Core.Recovery.RetainEnvelopeImport(source, digest, mutationToken))
            "recovery.importEnvelopeRetain"
            context

    let recordPreview admit maximumBytes (runtime: IWebRuntime) context =
        raw
            admit
            maximumBytes
            (fun source ->
                runtime.Core.Recovery.PreviewCanonicalRecordImport(source, requestToken context))
            (WebRecoveryWire.importQuery "recovery.importRecordPreview")
            context

    let recordRetain admit maximumBytes sourceDigestHeader (runtime: IWebRuntime) context =
        retain
            admit
            maximumBytes
            sourceDigestHeader
            (fun source digest ->
                runtime.Core.Recovery.RetainCanonicalRecordImport(source, digest, mutationToken))
            "recovery.importRecordRetain"
            context
