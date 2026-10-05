namespace ClaimCore.Hosting

open ClaimCore.Application
open ClaimCore.Domain

module internal RuntimeActorFacade =
    let private invalidInput =
        function
        | Rejection.Domain(DomainError.InvalidInput _) -> true
        | _ -> false

    let private prepare
        (admission: RuntimeAdmission)
        gate
        principal
        (inner: IActorClaimsCore)
        request
        ct
        =
        admission.RunClassified(
            (fun () -> inner.Prepare(request, ct)),
            (function
            | PrepareOutcome.PrepareRejected(_, rejection) when invalidInput rejection -> true
            | PrepareOutcome.CancelledBeforeAdmission _ -> true
            | _ -> false),
            ActorMutationDisclosure.prepare gate principal request,
            cancellationToken = ct,
            onCancelled = (fun () -> PrepareOutcome.CancelledBeforeAdmission request.OperationId)
        )


    type private GuardedRecovery
        (admission: RuntimeAdmission, gate, principal, inner: IRecoveryWorkflow) =
        interface IRecoveryWorkflow with
            member _.List(view, after, limit, ct) =
                admission.RunRead(
                    (fun () -> inner.List(view, after, limit, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> RecoveryQueryOutcome.RecoveryCancelled)
                )

            member _.Inspect(operationId, after, limit, ct) =
                admission.RunRead(
                    (fun () -> inner.Inspect(operationId, after, limit, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> RecoveryQueryOutcome.RecoveryCancelled)
                )

            member _.Resolve(operationId, digest, ct) =
                admission.RunDisclosing(
                    (fun () -> inner.Resolve(operationId, digest, ct)),
                    ActorMutationDisclosure.resolve gate principal operationId,
                    cancellationToken = ct,
                    onCancelled =
                        (fun () -> ResolveOutcome.ResolveCancelledBeforeAdmission operationId)
                )

            member _.Dismiss(operationId, digest, confirmed, ct) =
                admission.RunDisclosing(
                    (fun () -> inner.Dismiss(operationId, digest, confirmed, ct)),
                    ActorMutationDisclosure.dismiss gate principal operationId,
                    cancellationToken = ct,
                    onCancelled =
                        (fun () ->
                            RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId)
                )

            member _.ExportEnvelope(operationId, digest, ct) =
                admission.RunDisclosing(
                    (fun () -> inner.ExportEnvelope(operationId, digest, ct)),
                    ActorMutationDisclosure.export gate principal operationId,
                    cancellationToken = ct,
                    onCancelled = (fun () -> RecoveryQueryOutcome.RecoveryCancelled)
                )

            member _.PreviewEnvelopeImport(source, ct) =
                admission.RunRead(
                    (fun () -> inner.PreviewEnvelopeImport(source, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> RecoveryQueryOutcome.RecoveryCancelled)
                )

            member _.RetainEnvelopeImport(source, digest, ct) =
                admission.RunDisclosing(
                    (fun () -> inner.RetainEnvelopeImport(source, digest, ct)),
                    ActorMutationDisclosure.retain gate principal,
                    cancellationToken = ct,
                    onCancelled =
                        (fun () -> RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission)
                )

    let private wrapRecovery admission gate principal inner : IRecoveryWorkflow =
        new GuardedRecovery(admission, gate, principal, inner) :> IRecoveryWorkflow

    let private wrapManagement (admission: RuntimeAdmission) (inner: IActorManagement) =
        { new IActorManagement with
            member _.RegisterActor(eventId, target, ct) =
                admission.RunAuthoritySetup(
                    (fun () -> inner.RegisterActor(eventId, target, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> ActorManagementOutcome.ResourceUnavailable)
                )

            member _.SetGrant(eventId, target, role, scope, active, ct) =
                admission.RunAuthoritySetup(
                    (fun () -> inner.SetGrant(eventId, target, role, scope, active, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> ActorManagementOutcome.ResourceUnavailable)
                )

            member _.SetEnabled(eventId, target, enabled, ct) =
                admission.RunAuthoritySetup(
                    (fun () -> inner.SetEnabled(eventId, target, enabled, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> ActorManagementOutcome.ResourceUnavailable)
                )

            member _.Observe(eventId, ct) =
                admission.RunAuthoritySetup(
                    (fun () -> inner.Observe(eventId, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> ActorManagementOutcome.ResourceUnavailable)
                )
        }

    let private wrapLifecycle (admission: RuntimeAdmission) (inner: ICaseLifecycleWorkflow) =
        { new ICaseLifecycleWorkflow with
            member _.Review(reference, ct) =
                admission.RunRead(
                    (fun () -> inner.Review(reference, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> LifecycleReviewOutcome.Cancelled)
                )

            member _.Apply(change, ct) =
                admission.Run(
                    (fun () -> inner.Apply(change, ct)),
                    cancellationToken = ct,
                    onCancelled =
                        (fun () -> LifecycleWriteOutcome.CancelledBeforeAdmission change.EventId)
                )

            member _.Approve(change, approvalId, expiresAt, ct) =
                admission.Run(
                    (fun () -> inner.Approve(change, approvalId, expiresAt, ct)),
                    cancellationToken = ct,
                    onCancelled =
                        (fun () -> LifecycleWriteOutcome.CancelledBeforeAdmission approvalId)
                )
        }

    let private wrapTombstones (admission: RuntimeAdmission) (inner: ITombstoneWorkflow) =
        { new ITombstoneWorkflow with
            member _.Review(caseId, ct) =
                admission.RunRead(
                    (fun () -> inner.Review(caseId, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> TombstoneReviewOutcome.Cancelled)
                )

            member _.ApproveWitnessPrune(proposal, approvalId, expiresAt, ct) =
                admission.Run(
                    (fun () -> inner.ApproveWitnessPrune(proposal, approvalId, expiresAt, ct)),
                    cancellationToken = ct,
                    onCancelled =
                        (fun () -> TombstoneWriteOutcome.CancelledBeforeAdmission approvalId)
                )

            member _.ApproveTerminal(proposal, approvalId, expiresAt, ct) =
                admission.Run(
                    (fun () -> inner.ApproveTerminal(proposal, approvalId, expiresAt, ct)),
                    cancellationToken = ct,
                    onCancelled =
                        (fun () -> TombstoneWriteOutcome.CancelledBeforeAdmission approvalId)
                )

            member _.ChangeHold(change, ct) =
                admission.Run(
                    (fun () -> inner.ChangeHold(change, ct)),
                    cancellationToken = ct,
                    onCancelled =
                        (fun () -> TombstoneWriteOutcome.CancelledBeforeAdmission change.EventId)
                )
        }

    type private GuardedCore(admission: RuntimeAdmission, gate, principal, inner: IActorClaimsCore)
        =
        interface IActorClaimsCore with
            member _.Definition(ct) =
                admission.RunAuthorityRead(
                    (fun () -> inner.Definition ct),
                    cancellationToken = ct,
                    onCancelled = (fun () -> QueryOutcome.Cancelled)
                )

            member _.Prepare(request, ct) =
                prepare admission gate principal inner request ct

            member _.Execute(request, ct) =
                admission.RunClassified(
                    (fun () -> inner.Execute(request, ct)),
                    (function
                    | SubmissionOutcome.RejectedBeforeAttempt(None, rejection) when
                        invalidInput rejection
                        ->
                        true
                    | SubmissionOutcome.CancelledBeforeAdmission _ -> true
                    | _ -> false),
                    ActorMutationDisclosure.submit gate principal request,
                    cancellationToken = ct,
                    onCancelled =
                        (fun () -> SubmissionOutcome.CancelledBeforeAdmission request.OperationId)
                )

            member _.Get(reference, ct) =
                admission.RunRead(
                    (fun () -> inner.Get(reference, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> QueryOutcome.Cancelled)
                )

            member _.List(request, ct) =
                admission.RunRead(
                    (fun () -> inner.List(request, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> QueryOutcome.Cancelled)
                )

            member _.History(request, ct) =
                admission.RunRead(
                    (fun () -> inner.History(request, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> QueryOutcome.Cancelled)
                )

            member _.ObserveOperation(operationId, ct) =
                admission.RunRead(
                    (fun () -> inner.ObserveOperation(operationId, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> QueryOutcome.Cancelled)
                )

            member _.Recovery = wrapRecovery admission gate principal inner.Recovery
            member _.Management = wrapManagement admission inner.Management
            member _.Lifecycle = wrapLifecycle admission inner.Lifecycle
            member _.Tombstones = wrapTombstones admission inner.Tombstones

            member _.ApproveCopySigner(request, ct) =
                admission.RunAuthoritySetup(
                    (fun () -> inner.ApproveCopySigner(request, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> CopySignerApprovalOutcome.ResourceUnavailable)
                )

            member _.ApproveRealDataActivation(request, ct) =
                admission.RunAuthoritySetup(
                    (fun () -> inner.ApproveRealDataActivation(request, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> RealDataActivationApprovalOutcome.ResourceUnavailable)
                )

            member _.ReviewRealDataActivation(planId, ct) =
                admission.RunAuthoritySetup(
                    (fun () -> inner.ReviewRealDataActivation(planId, ct)),
                    cancellationToken = ct,
                    onCancelled =
                        (fun () -> RealDataActivationPlanReviewOutcome.ResourceUnavailable)
                )

            member _.ApproveCopyDeletion(request, ct) =
                admission.Run(
                    (fun () -> inner.ApproveCopyDeletion(request, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> CopyDeletionApprovalOutcome.ResourceUnavailable)
                )

            member _.ApproveCopyAdoption(request, ct) =
                admission.RunAuthoritySetup(
                    (fun () -> inner.ApproveCopyAdoption(request, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> CopyAdoptionApprovalOutcome.ResourceUnavailable)
                )

            member _.ApproveWriterHandoff(request, ct) =
                admission.RunAuthoritySetup(
                    (fun () -> inner.ApproveWriterHandoff(request, ct)),
                    cancellationToken = ct,
                    onCancelled = (fun () -> WriterHandoffApprovalOutcome.ResourceUnavailable)
                )

    let wrap admission gate principal inner : IActorClaimsCore =
        new GuardedCore(admission, gate, principal, inner) :> IActorClaimsCore
