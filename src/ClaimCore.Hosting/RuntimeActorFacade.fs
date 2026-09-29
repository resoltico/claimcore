namespace ClaimCore.Hosting

open ClaimCore.Application
open ClaimCore.Domain

module internal RuntimeActorFacade =
    let private invalidInput =
        function
        | Rejection.Domain(DomainError.InvalidInput _) -> true
        | _ -> false

    let private wrapRecovery (admission: RuntimeAdmission) (inner: IRecoveryWorkflow) =
        { new IRecoveryWorkflow with
            member _.List(view, after, limit, ct) =
                admission.RunRead(fun () -> inner.List(view, after, limit, ct))

            member _.Inspect(operationId, after, limit, ct) =
                admission.RunRead(fun () -> inner.Inspect(operationId, after, limit, ct))

            member _.Resolve(operationId, digest, ct) =
                admission.Run(fun () -> inner.Resolve(operationId, digest, ct))

            member _.Dismiss(operationId, digest, confirmed, ct) =
                admission.Run(fun () -> inner.Dismiss(operationId, digest, confirmed, ct))

            member _.ExportEnvelope(operationId, digest, ct) =
                admission.Run(fun () -> inner.ExportEnvelope(operationId, digest, ct))

            member _.PreviewEnvelopeImport(source, ct) =
                admission.RunRead(fun () -> inner.PreviewEnvelopeImport(source, ct))

            member _.RetainEnvelopeImport(source, digest, ct) =
                admission.Run(fun () -> inner.RetainEnvelopeImport(source, digest, ct))
        }

    let private wrapManagement (admission: RuntimeAdmission) (inner: IActorManagement) =
        { new IActorManagement with
            member _.RegisterActor(eventId, target, ct) =
                admission.RunAuthoritySetup(fun () -> inner.RegisterActor(eventId, target, ct))

            member _.SetGrant(eventId, target, role, scope, active, ct) =
                admission.RunAuthoritySetup(fun () ->
                    inner.SetGrant(eventId, target, role, scope, active, ct))

            member _.SetEnabled(eventId, target, enabled, ct) =
                admission.RunAuthoritySetup(fun () ->
                    inner.SetEnabled(eventId, target, enabled, ct))

            member _.Observe(eventId, ct) =
                admission.RunAuthoritySetup(fun () -> inner.Observe(eventId, ct))
        }

    let private wrapLifecycle (admission: RuntimeAdmission) (inner: ICaseLifecycleWorkflow) =
        { new ICaseLifecycleWorkflow with
            member _.Review(reference, ct) =
                admission.RunRead(fun () -> inner.Review(reference, ct))

            member _.Apply(change, ct) =
                admission.Run(fun () -> inner.Apply(change, ct))

            member _.Approve(change, approvalId, expiresAt, ct) =
                admission.Run(fun () -> inner.Approve(change, approvalId, expiresAt, ct))
        }

    let private wrapTombstones (admission: RuntimeAdmission) (inner: ITombstoneWorkflow) =
        { new ITombstoneWorkflow with
            member _.Review(caseId, ct) =
                admission.RunRead(fun () -> inner.Review(caseId, ct))

            member _.ApproveWitnessPrune(proposal, approvalId, expiresAt, ct) =
                admission.Run(fun () ->
                    inner.ApproveWitnessPrune(proposal, approvalId, expiresAt, ct))

            member _.ApproveTerminal(proposal, approvalId, expiresAt, ct) =
                admission.Run(fun () -> inner.ApproveTerminal(proposal, approvalId, expiresAt, ct))

            member _.ChangeHold(change, ct) =
                admission.Run(fun () -> inner.ChangeHold(change, ct))
        }

    let wrap (admission: RuntimeAdmission) (inner: IActorClaimsCore) : IActorClaimsCore =
        { new IActorClaimsCore with
            member _.Definition(ct) =
                admission.RunAuthorityRead(fun () -> inner.Definition ct)

            member _.Prepare(request, ct) =
                admission.RunClassified(
                    (fun () -> inner.Prepare(request, ct)),
                    (function
                    | PrepareOutcome.PrepareRejected(_, rejection) when invalidInput rejection ->
                        true
                    | PrepareOutcome.CancelledBeforeAdmission _ -> true
                    | _ -> false)
                )

            member _.Execute(request, ct) =
                admission.RunClassified(
                    (fun () -> inner.Execute(request, ct)),
                    (function
                    | SubmissionOutcome.RejectedBeforeAttempt(None, rejection) when
                        invalidInput rejection
                        ->
                        true
                    | SubmissionOutcome.CancelledBeforeAdmission _ -> true
                    | _ -> false)
                )

            member _.Get(reference, ct) =
                admission.RunRead(fun () -> inner.Get(reference, ct))

            member _.List(request, ct) =
                admission.RunRead(fun () -> inner.List(request, ct))

            member _.History(request, ct) =
                admission.RunRead(fun () -> inner.History(request, ct))

            member _.ObserveOperation(operationId, ct) =
                admission.RunRead(fun () -> inner.ObserveOperation(operationId, ct))

            member _.Recovery = wrapRecovery admission inner.Recovery
            member _.Management = wrapManagement admission inner.Management
            member _.Lifecycle = wrapLifecycle admission inner.Lifecycle
            member _.Tombstones = wrapTombstones admission inner.Tombstones

            member _.ApproveCopySigner(request, ct) =
                admission.RunAuthoritySetup(fun () -> inner.ApproveCopySigner(request, ct))

            member _.ApproveRealDataActivation(request, ct) =
                admission.RunAuthoritySetup(fun () -> inner.ApproveRealDataActivation(request, ct))

            member _.ReviewRealDataActivation(planId, ct) =
                admission.RunAuthoritySetup(fun () -> inner.ReviewRealDataActivation(planId, ct))

            member _.ApproveCopyDeletion(request, ct) =
                admission.Run(fun () -> inner.ApproveCopyDeletion(request, ct))

            member _.ApproveCopyAdoption(request, ct) =
                admission.RunAuthoritySetup(fun () -> inner.ApproveCopyAdoption(request, ct))

            member _.ApproveWriterHandoff(request, ct) =
                admission.RunAuthoritySetup(fun () -> inner.ApproveWriterHandoff(request, ct))
        }
