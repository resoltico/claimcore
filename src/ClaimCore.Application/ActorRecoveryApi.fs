namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks

/// Recovery never receives a reusable store. Each operation obtains a fresh, scoped actor call.
module internal ActorRecoveryApi =
    let private boundedSource (source: byte array) =
        not (Object.ReferenceEquals(box source, null))
        && source.Length > 0
        && int64 source.Length
           <= int64 SemanticContract.current.RequestByteLimit * 3L + 4096L

    let private admit
        (gate: IActorGate)
        (principal: PrincipalKey)
        action
        operationId
        (ct: CancellationToken)
        =
        task {
            try
                let! context = gate.Operation(principal, action, operationId, ct)
                return Choice1Of3 context
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return Choice2Of3()
            | _ -> return Choice3Of3()
        }

    let private query
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        action
        operationId
        (invoke: ActorCallContext -> IRecoveryWorkflow -> Task<RecoveryQueryOutcome<'value>>)
        ct
        =
        task {
            let! admitted = admit gate principal action operationId ct

            match admitted with
            | Choice1Of3(Some context) -> return! invoke context (factory context).Recovery
            | Choice2Of3() -> return RecoveryQueryOutcome.RecoveryCancelled
            | Choice3Of3() -> return RecoveryQueryOutcome.RecoveryFailed CoreFault.StoreUnavailable
            | Choice1Of3 None ->
                return RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable
        }

    let private resolve
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        operationId
        digest
        ct
        =
        task {
            let! admitted = admit gate principal EndpointAction.RecoveryResolve operationId ct

            match admitted with
            | Choice1Of3(Some context) ->
                return! (factory context).Recovery.Resolve(operationId, digest, ct)
            | Choice2Of3() -> return ResolveOutcome.ResolveCancelledBeforeAdmission operationId
            | Choice3Of3() ->
                return ResolveOutcome.ResolveFailedBeforeAttempt(None, CoreFault.StoreUnavailable)
            | Choice1Of3 None ->
                return
                    ResolveOutcome.RefusedBeforeAttempt(None, RecoveryRejection.ResourceUnavailable)
        }

    let private dismiss
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        operationId
        digest
        confirmed
        ct
        =
        task {
            let! admitted = admit gate principal EndpointAction.RecoveryDismiss operationId ct

            match admitted with
            | Choice1Of3(Some context) ->
                return! (factory context).Recovery.Dismiss(operationId, digest, confirmed, ct)
            | Choice2Of3() ->
                return RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId
            | Choice3Of3() -> return RecoveryDismissOutcome.DismissFailed CoreFault.StoreUnavailable
            | Choice1Of3 None ->
                return
                    RecoveryDismissOutcome.DismissRefused(
                        None,
                        RecoveryRejection.ResourceUnavailable
                    )
        }

    let private previewImport
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        source
        ct
        =
        task {
            try
                let! installation =
                    gate.Installation(principal, EndpointAction.RecoveryImportPreview, ct)

                match installation with
                | None ->
                    return
                        RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable
                | Some _ when not (boundedSource source) ->
                    return
                        RecoveryQueryOutcome.RecoveryRejected
                            RecoveryRejection.EnvelopeInvalidOrUnsupported
                | Some context ->
                    let stable = Array.copy source

                    try
                        let! result = (factory context).Recovery.PreviewEnvelopeImport(stable, ct)

                        match result with
                        | RecoveryQueryOutcome.RecoverySucceeded preview ->
                            let! scoped =
                                gate.Import(
                                    principal,
                                    preview.CaseId,
                                    preview.DecodedEffect.OperationId,
                                    preview.DecodedEffect.CaseReference,
                                    ct
                                )

                            match scoped with
                            | Some _ -> return result
                            | None ->
                                return
                                    RecoveryQueryOutcome.RecoveryRejected
                                        RecoveryRejection.ResourceUnavailable
                        | _ -> return result
                    finally
                        CryptographicOperations.ZeroMemory(stable)
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                return RecoveryQueryOutcome.RecoveryCancelled
            | _ -> return RecoveryQueryOutcome.RecoveryFailed CoreFault.StoreUnavailable
        }

    let private retainImport
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        source
        digest
        ct
        =
        task {
            if not (boundedSource source) then
                return
                    RecoveryImportRetainOutcome.ImportRejected
                        RecoveryRejection.EnvelopeInvalidOrUnsupported
            else
                let stable = Array.copy source

                try
                    try
                        match! previewImport gate factory principal stable ct with
                        | RecoveryQueryOutcome.RecoveryCancelled ->
                            return RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
                        | RecoveryQueryOutcome.RecoveryFailed fault ->
                            return RecoveryImportRetainOutcome.ImportFailed fault
                        | RecoveryQueryOutcome.RecoveryRejected reason ->
                            return RecoveryImportRetainOutcome.ImportRejected reason
                        | RecoveryQueryOutcome.RecoverySucceeded preview ->
                            let! scoped =
                                gate.Import(
                                    principal,
                                    preview.CaseId,
                                    preview.DecodedEffect.OperationId,
                                    preview.DecodedEffect.CaseReference,
                                    ct
                                )

                            match scoped with
                            | None ->
                                return
                                    RecoveryImportRetainOutcome.ImportRejected
                                        RecoveryRejection.ResourceUnavailable
                            | Some context ->
                                return!
                                    (factory context)
                                        .Recovery.RetainEnvelopeImport(stable, digest, ct)
                    with
                    | :? OperationCanceledException when ct.IsCancellationRequested ->
                        return RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
                    | _ ->
                        return RecoveryImportRetainOutcome.ImportFailed CoreFault.StoreUnavailable
                finally
                    CryptographicOperations.ZeroMemory(stable)
        }

    let private list
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        view
        cursor
        limit
        ct
        =
        task {
            try
                let! admitted = gate.Installation(principal, EndpointAction.RecoveryList, ct)

                match admitted with
                | Some context -> return! (factory context).Recovery.List(view, cursor, limit, ct)
                | None ->
                    return
                        RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                return RecoveryQueryOutcome.RecoveryCancelled
            | _ -> return RecoveryQueryOutcome.RecoveryFailed CoreFault.StoreUnavailable
        }

    let create
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        : IRecoveryWorkflow =
        { new IRecoveryWorkflow with
            member _.List(view, cursor, limit, ct) =
                list gate factory principal view cursor limit ct

            member _.Inspect(operationId, after, limit, ct) =
                query
                    gate
                    factory
                    principal
                    EndpointAction.RecoveryInspect
                    operationId
                    (fun context (recovery: IRecoveryWorkflow) ->
                        task {
                            let! result = recovery.Inspect(operationId, after, limit, ct)

                            return
                                TypedProjection.actorInspection
                                    context.AllowedRecoveryActions
                                    result
                        })
                    ct

            member _.Resolve(operationId, requestSha256, ct) =
                resolve gate factory principal operationId requestSha256 ct

            member _.Dismiss(operationId, requestSha256, confirmed, ct) =
                dismiss gate factory principal operationId requestSha256 confirmed ct

            member _.ExportEnvelope(operationId, requestSha256, ct) =
                query
                    gate
                    factory
                    principal
                    EndpointAction.RecoveryExport
                    operationId
                    (fun _ (recovery: IRecoveryWorkflow) ->
                        recovery.ExportEnvelope(operationId, requestSha256, ct))
                    ct

            member _.PreviewEnvelopeImport(source, ct) =
                previewImport gate factory principal source ct

            member _.RetainEnvelopeImport(source, digest, ct) =
                retainImport gate factory principal source digest ct
        }
