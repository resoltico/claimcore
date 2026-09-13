namespace ClaimCore.Hosting

open ClaimCore.Application

module internal RuntimeCoreFacade =
    let private recovery (admission: RuntimeAdmission) (inner: IRecoveryWorkflow) =
        { new IRecoveryWorkflow with
            member _.List(after, limit, cancellationToken) =
                admission.Run(fun () -> inner.List(after, limit, cancellationToken))

            member _.Inspect(operationId, cancellationToken) =
                admission.Run(fun () -> inner.Inspect(operationId, cancellationToken))

            member _.Resolve(operationId, digest, cancellationToken) =
                admission.Run(fun () -> inner.Resolve(operationId, digest, cancellationToken))

            member _.Dismiss(operationId, digest, confirmed, cancellationToken) =
                admission.Run(fun () ->
                    inner.Dismiss(operationId, digest, confirmed, cancellationToken))

            member _.ExportEnvelope(operationId, digest, cancellationToken) =
                admission.Run(fun () ->
                    inner.ExportEnvelope(operationId, digest, cancellationToken))

            member _.PreviewEnvelopeImport(source, cancellationToken) =
                admission.Run(fun () -> inner.PreviewEnvelopeImport(source, cancellationToken))

            member _.RetainEnvelopeImport(source, digest, cancellationToken) =
                admission.Run(fun () ->
                    inner.RetainEnvelopeImport(source, digest, cancellationToken))

            member _.PreviewCanonicalRecordImport(source, cancellationToken) =
                admission.Run(fun () ->
                    inner.PreviewCanonicalRecordImport(source, cancellationToken))

            member _.RetainCanonicalRecordImport(source, digest, cancellationToken) =
                admission.Run(fun () ->
                    inner.RetainCanonicalRecordImport(source, digest, cancellationToken))
        }

    let wrap (admission: RuntimeAdmission) (inner: IClaimsCore) : IClaimsCore =
        let guardedRecovery = recovery admission inner.Recovery

        { new IClaimsCore with
            member _.Describe() =
                use _lease = admission.Admit()
                inner.Describe()

            member _.Prepare(draft, cancellationToken) =
                admission.Run(fun () -> inner.Prepare(draft, cancellationToken))

            member _.Execute(draft, cancellationToken) =
                admission.Run(fun () -> inner.Execute(draft, cancellationToken))

            member _.Get(reference, cancellationToken) =
                admission.Run(fun () -> inner.Get(reference, cancellationToken))

            member _.List(request, cancellationToken) =
                admission.Run(fun () -> inner.List(request, cancellationToken))

            member _.History(request, cancellationToken) =
                admission.Run(fun () -> inner.History(request, cancellationToken))

            member _.ObserveOperation(operationId, cancellationToken) =
                admission.Run(fun () -> inner.ObserveOperation(operationId, cancellationToken))

            member _.Recovery = guardedRecovery
        }
