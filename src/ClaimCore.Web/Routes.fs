namespace ClaimCore.Web

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Application
open ClaimCore.Contracts

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
                    RouteSupport.markDispatched context
                    let! outcome = invoke input
                    RouteSupport.markCompleted context
                    return project outcome
        }

    let get admit maximumBytes (core: IClaimsCore) context =
        json
            admit
            maximumBytes
            HttpInput.caseReference
            (fun reference -> core.Get(reference, requestToken context))
            WebWire.get
            context

    let list admit maximumBytes (core: IClaimsCore) context =
        json
            admit
            maximumBytes
            (HttpInput.page (core.Describe()).Contract.MaximumPageSize)
            (fun input ->
                core.List(
                    {
                        AfterReference = input.Cursor
                        Limit = input.Limit
                    },
                    requestToken context
                ))
            WebWire.list
            context

    let history admit maximumBytes (core: IClaimsCore) context =
        json
            admit
            maximumBytes
            (HttpInput.history (core.Describe()).Contract.MaximumPageSize)
            (fun input ->
                core.History(
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

    let observe admit maximumBytes (core: IClaimsCore) context =
        json
            admit
            maximumBytes
            HttpInput.operationId
            (fun operationId -> core.ObserveOperation(operationId, requestToken context))
            WebWire.observe
            context

    let prepare admit maximumBytes (core: IClaimsCore) context =
        json
            admit
            maximumBytes
            HttpInput.draft
            (fun draft ->
                match Drafts.bindForEndpoint draft with
                | Ok request -> core.Prepare(request, mutationToken)
                | Error rejection ->
                    Task.FromResult(PrepareOutcome.PrepareRejected(draft.OperationId, rejection)))
            (WebWire.prepare "command.prepare")
            context

    let submit admit maximumBytes (core: IClaimsCore) context =
        json
            admit
            maximumBytes
            HttpInput.resolve
            (fun input ->
                core.Recovery.Resolve(input.OperationId, input.RequestSha256, mutationToken))
            (WebRecoveryWire.resolve "command.execute")
            context

    let recoveryList admit maximumBytes (core: IClaimsCore) context =
        json
            admit
            maximumBytes
            (HttpInput.recoveryPage (core.Describe()).Contract.MaximumPageSize)
            (fun input ->
                core.Recovery.List(input.View, input.Cursor, input.Limit, requestToken context))
            WebRecoveryWire.list
            context

    let recoveryInspect admit maximumBytes (core: IClaimsCore) context =
        json
            admit
            maximumBytes
            (HttpInput.recoveryInspect (core.Describe()).Contract.MaximumPageSize)
            (fun input ->
                core.Recovery.Inspect(
                    input.OperationId,
                    input.AttemptCursor,
                    input.AttemptLimit,
                    requestToken context
                ))
            WebRecoveryWire.inspect
            context

    let recoveryResolve admit maximumBytes (core: IClaimsCore) context =
        json
            admit
            maximumBytes
            HttpInput.resolve
            (fun input ->
                core.Recovery.Resolve(input.OperationId, input.RequestSha256, mutationToken))
            (WebRecoveryWire.resolve "recovery.resolve")
            context

    let recoveryDismiss admit maximumBytes (core: IClaimsCore) context =
        json
            admit
            maximumBytes
            HttpInput.dismiss
            (fun input ->
                core.Recovery.Dismiss(
                    input.OperationId,
                    input.RequestSha256,
                    input.Confirmed,
                    mutationToken
                ))
            WebRecoveryWire.dismiss
            context

    let recoveryExport admit maximumBytes (core: IClaimsCore) context =
        task {
            match! RouteSupport.admittedBody admit maximumBytes context with
            | Error result -> return result
            | Ok bytes ->
                match HttpInput.resolve bytes with
                | Error message -> return RouteSupport.inputFailure context message
                | Ok input ->
                    RouteSupport.markDispatched context

                    let! exported =
                        core.Recovery.ExportEnvelope(
                            input.OperationId,
                            input.RequestSha256,
                            requestToken context
                        )

                    RouteSupport.markCompleted context

                    match exported with
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
                                    WebHostFailure.ExportMetadataInvalid
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
                RouteSupport.markDispatched context
                let! outcome = invoke source
                RouteSupport.markCompleted context
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
                    RouteSupport.markDispatched context
                    let! outcome = invoke source digest
                    RouteSupport.markCompleted context
                    return WebRecoveryWire.importRetain endpoint outcome
        }

    let envelopePreview admit maximumBytes (core: IClaimsCore) context =
        raw
            admit
            maximumBytes
            (fun source -> core.Recovery.PreviewEnvelopeImport(source, requestToken context))
            (WebRecoveryWire.importQuery "recovery.importEnvelopePreview")
            context

    let envelopeRetain admit maximumBytes sourceDigestHeader (core: IClaimsCore) context =
        retain
            admit
            maximumBytes
            sourceDigestHeader
            (fun source digest -> core.Recovery.RetainEnvelopeImport(source, digest, mutationToken))
            "recovery.importEnvelopeRetain"
            context

    let recordPreview admit maximumBytes (core: IClaimsCore) context =
        raw
            admit
            maximumBytes
            (fun source -> core.Recovery.PreviewCanonicalRecordImport(source, requestToken context))
            (WebRecoveryWire.importQuery "recovery.importRecordPreview")
            context

    let recordRetain admit maximumBytes sourceDigestHeader (core: IClaimsCore) context =
        retain
            admit
            maximumBytes
            sourceDigestHeader
            (fun source digest ->
                core.Recovery.RetainCanonicalRecordImport(source, digest, mutationToken))
            "recovery.importRecordRetain"
            context
