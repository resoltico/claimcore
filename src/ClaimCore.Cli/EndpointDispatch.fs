namespace ClaimCore.Cli

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Contracts

module EndpointDispatch =
    let private localFailure (endpoint: Endpoint) (code: FaultCode) (message: string) =
        CliWireCodec.localFailure
            (Endpoint.identifier endpoint)
            {
                Code = code
                Message = message
                Action = RecommendedAction.StopAndInvestigate
            }

    let private source (maximum: int) (path: string) : Result<byte array, ProtocolFailure> =
        PrivateFiles.readSource maximum path
        |> Result.mapError (fun message ->
            ProtocolFailure.create "PRIVATE_FILE_ERROR" message "/input/source")

    let private importPreview endpoint sourcePath maximum load =
        match source maximum sourcePath with
        | Error problem -> Task.FromResult(JsonResponse.protocolFailure 3 problem)
        | Ok bytes ->
            task {
                let! outcome = load bytes

                return CliWireCodec.importPreview (Endpoint.identifier endpoint) outcome
            }

    let private importRetain endpoint sourcePath digest maximum retain =
        match source maximum sourcePath with
        | Error problem -> Task.FromResult(JsonResponse.protocolFailure 3 problem)
        | Ok bytes ->
            task {
                let! outcome = retain bytes digest

                return CliWireCodec.importRetain (Endpoint.identifier endpoint) outcome
            }

    let private recoveryList (core: IClaimsCore) view cursor limit token =
        task {
            let! outcome = core.Recovery.List(view, cursor, limit, token)

            return CliWireCodec.recoveryList (Endpoint.identifier Endpoint.RecoveryList) outcome
        }

    let private recoveryInspect (core: IClaimsCore) operationId cursor limit token =
        task {
            let! outcome = core.Recovery.Inspect(operationId, cursor, limit, token)

            return
                CliWireCodec.recoveryInspect (Endpoint.identifier Endpoint.RecoveryInspect) outcome
        }

    let private recoveryResolve (core: IClaimsCore) operationId digest token =
        task {
            let! outcome = core.Recovery.Resolve(operationId, digest, token)

            return CliWireCodec.resolve (Endpoint.identifier Endpoint.RecoveryResolve) outcome
        }

    let private recoveryDismiss (core: IClaimsCore) operationId digest token =
        task {
            let! outcome = core.Recovery.Dismiss(operationId, digest, true, token)

            return CliWireCodec.dismiss (Endpoint.identifier Endpoint.RecoveryDismiss) outcome
        }

    let private recovery (core: IClaimsCore) endpoint input token =
        match endpoint, input with
        | Endpoint.RecoveryList, EndpointInput.RecoveryPage(view, cursor, limit) ->
            recoveryList core view cursor limit token
        | Endpoint.RecoveryInspect, EndpointInput.RecoveryInspect(operationId, cursor, limit) ->
            recoveryInspect core operationId cursor limit token
        | Endpoint.RecoveryResolve, EndpointInput.RecoveryResolve(operationId, digest) ->
            recoveryResolve core operationId digest token
        | Endpoint.RecoveryDismiss, EndpointInput.RecoveryDismiss(operationId, digest) ->
            recoveryDismiss core operationId digest token
        | Endpoint.RecoveryImportEnvelopePreview, EndpointInput.RecoveryImportPreview path ->
            importPreview endpoint path 131072 (fun bytes ->
                core.Recovery.PreviewEnvelopeImport(bytes, token))
        | Endpoint.RecoveryImportRecordPreview, EndpointInput.RecoveryImportPreview path ->
            importPreview endpoint path 65536 (fun bytes ->
                core.Recovery.PreviewCanonicalRecordImport(bytes, token))
        | Endpoint.RecoveryImportEnvelopeRetain, EndpointInput.RecoveryImportRetain(path, digest) ->
            importRetain endpoint path digest 131072 (fun bytes supplied ->
                core.Recovery.RetainEnvelopeImport(bytes, supplied, token))
        | Endpoint.RecoveryImportRecordRetain, EndpointInput.RecoveryImportRetain(path, digest) ->
            importRetain endpoint path digest 65536 (fun bytes supplied ->
                core.Recovery.RetainCanonicalRecordImport(bytes, supplied, token))
        | _ ->
            Task.FromResult(
                localFailure
                    endpoint
                    FaultCode.RecoveryIntegrityError
                    "The endpoint input does not match its generated contract."
            )

    let private export (core: IClaimsCore) operationId digest destination token =
        task {
            let! outcome = core.Recovery.ExportEnvelope(operationId, digest, token)

            match outcome with
            | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(artifact: RecoveryExport)) when
                artifact.RequestSha256 = digest
                ->
                match PrivateFiles.writeNew destination artifact.Bytes with
                | Ok() ->
                    return
                        CliWireCodec.exported
                            (Endpoint.identifier Endpoint.RecoveryExport)
                            operationId
                            artifact.RequestSha256
                            artifact.MediaType
                | Error message ->
                    return localFailure Endpoint.RecoveryExport FaultCode.StoreUnavailable message
            | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found _) ->
                return
                    CliWireCodec.exportIdentityConflict (
                        Endpoint.identifier Endpoint.RecoveryExport
                    )
            | RecoveryQueryOutcome.RecoveryRejected rejection when
                rejection.Code = RecoveryRejectionCode.RecoveryIdempotencyConflict
                ->
                return
                    CliWireCodec.exportIdentityConflict (
                        Endpoint.identifier Endpoint.RecoveryExport
                    )
            | _ ->
                return
                    CliWireCodec.recoveryExport
                        (Endpoint.identifier Endpoint.RecoveryExport)
                        operationId
                        outcome
        }

    let private currentCase (core: IClaimsCore) reference token =
        task {
            let! outcome = core.Get(reference, token)

            return CliWireCodec.caseGet (Endpoint.identifier Endpoint.CaseGet) outcome
        }

    let private caseList (core: IClaimsCore) cursor limit token =
        task {
            let! outcome =
                core.List(
                    {
                        AfterReference = cursor
                        Limit = limit
                    },
                    token
                )

            return CliWireCodec.caseList (Endpoint.identifier Endpoint.CaseList) outcome
        }

    let private caseHistory (core: IClaimsCore) reference cursor limit detail token =
        task {
            let! outcome =
                core.History(
                    {
                        CaseReference = reference
                        AfterCursor = cursor
                        Limit = limit
                        Detail = detail
                    },
                    token
                )

            return CliWireCodec.caseHistory (Endpoint.identifier Endpoint.CaseHistory) outcome
        }

    let private observeOperation (core: IClaimsCore) operationId token =
        task {
            let! outcome = core.ObserveOperation(operationId, token)

            return
                CliWireCodec.operationObserve
                    (Endpoint.identifier Endpoint.OperationObserve)
                    outcome
        }

    let private query (core: IClaimsCore) endpoint input token =
        match endpoint, input with
        | Endpoint.CaseGet, EndpointInput.CaseReference reference ->
            currentCase core reference token
        | Endpoint.CaseList, EndpointInput.CaseList(cursor, limit) ->
            caseList core cursor limit token
        | Endpoint.CaseHistory, EndpointInput.History(reference, cursor, limit, detail) ->
            caseHistory core reference cursor limit detail token
        | Endpoint.OperationObserve, EndpointInput.Operation operationId ->
            observeOperation core operationId token
        | _ ->
            Task.FromResult(
                localFailure
                    endpoint
                    FaultCode.StoreIntegrityError
                    "The endpoint input does not match its generated contract."
            )

    let private command (core: IClaimsCore) endpoint input token =
        match endpoint, input with
        | Endpoint.CommandPrepare, EndpointInput.Draft draft ->
            task {
                let! outcome =
                    match Drafts.bindForEndpoint draft with
                    | Ok request -> core.Prepare(request, token)
                    | Error rejection ->
                        Task.FromResult(
                            PrepareOutcome.PrepareRejected(draft.OperationId, rejection)
                        )

                return CliWireCodec.prepare (Endpoint.identifier endpoint) outcome
            }
        | Endpoint.CommandExecute, EndpointInput.Draft draft ->
            task {
                let! outcome =
                    match Drafts.bindForEndpoint draft with
                    | Ok request -> core.Execute(request, token)
                    | Error rejection ->
                        Task.FromResult(SubmissionOutcome.RejectedBeforeAttempt(None, rejection))

                return CliWireCodec.submission (Endpoint.identifier endpoint) outcome
            }
        | _ ->
            Task.FromResult(
                localFailure
                    endpoint
                    FaultCode.StoreIntegrityError
                    "The endpoint input does not match its generated contract."
            )

    let private queryEndpoints =
        Set.ofList
            [
                Endpoint.CaseGet
                Endpoint.CaseList
                Endpoint.CaseHistory
                Endpoint.OperationObserve
            ]

    let private commandEndpoints =
        Set.ofList [ Endpoint.CommandPrepare; Endpoint.CommandExecute ]

    let execute (core: IClaimsCore) endpoint input (token: CancellationToken) =
        if Set.contains endpoint commandEndpoints then
            command core endpoint input token
        elif Set.contains endpoint queryEndpoints then
            query core endpoint input token
        else
            match endpoint, input with
            | Endpoint.RecoveryExport,
              EndpointInput.RecoveryExport(operationId, digest, destination) ->
                export core operationId digest destination token
            | _ -> recovery core endpoint input token
