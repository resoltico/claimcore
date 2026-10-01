namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

/// The only application entry point for CLI, Web, and native callers. Composition supplies narrow
/// claim and recovery ports; no caller receives persistence objects or transition callbacks.
type internal IClaimsCore =
    abstract Describe: unit -> CoreDescription
    abstract Prepare: CommandRequest * CancellationToken -> Task<PrepareOutcome>
    abstract Execute: CommandRequest * CancellationToken -> Task<SubmissionOutcome>

    abstract Get: string * CancellationToken -> Task<QueryOutcome<Lookup<CurrentCase, string>>>

    abstract List: CaseListRequest * CancellationToken -> Task<QueryOutcome<CaseSummaryPage>>

    abstract History:
        HistoryRequest * CancellationToken -> Task<QueryOutcome<Lookup<HistoryResultPage, string>>>

    abstract ObserveOperation:
        Guid * CancellationToken -> Task<QueryOutcome<Lookup<OperationReceipt, Guid>>>

    abstract Recovery: IRecoveryWorkflow

type IActorClaimsCore =
    abstract Definition: CancellationToken -> Task<QueryOutcome<CoreDescription>>
    abstract Prepare: CommandRequest * CancellationToken -> Task<PrepareOutcome>
    abstract Execute: CommandRequest * CancellationToken -> Task<SubmissionOutcome>
    abstract Get: string * CancellationToken -> Task<QueryOutcome<Lookup<CurrentCase, string>>>
    abstract List: CaseListRequest * CancellationToken -> Task<QueryOutcome<CaseSummaryPage>>

    abstract History:
        HistoryRequest * CancellationToken -> Task<QueryOutcome<Lookup<HistoryResultPage, string>>>

    abstract ObserveOperation:
        Guid * CancellationToken -> Task<QueryOutcome<Lookup<OperationReceipt, Guid>>>

    abstract Recovery: IRecoveryWorkflow
    abstract Management: IActorManagement
    abstract Lifecycle: ICaseLifecycleWorkflow
    abstract Tombstones: ITombstoneWorkflow

    abstract ApproveCopySigner:
        CopySignerApprovalRequest * CancellationToken -> Task<CopySignerApprovalOutcome>

    abstract ApproveCopyDeletion:
        CopyDeletionApprovalRequest * CancellationToken -> Task<CopyDeletionApprovalOutcome>

    abstract ApproveCopyAdoption:
        CopyAdoptionApprovalRequest * CancellationToken -> Task<CopyAdoptionApprovalOutcome>

    abstract ApproveWriterHandoff:
        WriterHandoffApprovalRequest * CancellationToken -> Task<WriterHandoffApprovalOutcome>

    abstract ApproveRealDataActivation:
        RealDataActivationApprovalRequest * CancellationToken ->
            Task<RealDataActivationApprovalOutcome>

    abstract ReviewRealDataActivation:
        planId: Guid * CancellationToken -> Task<RealDataActivationPlanReviewOutcome>

module internal CoreApi =
    let private prepareForActor store recovery clock commandAuthority request cancellationToken =
        match commandAuthority with
        | Some authority ->
            TypedPreparation.prepare store recovery clock authority request cancellationToken
        | None ->
            Task.FromResult(
                PrepareOutcome.PrepareRejected(request.OperationId, Rejection.ResourceUnavailable)
            )

    let createActor
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessTime)
        (context: ActorCallContext)
        (artifactAuthority: IRecoveryArtifactAuthority)
        : IClaimsCore =
        BuildIdentity.requireCompatibleAssembly typeof<Claim>.Assembly

        let recoveryWorkflow =
            RecoveryCoordinator.create store recovery clock context.Binding artifactAuthority

        let commandAuthority =
            context.CaseId
            |> Option.map (fun caseId ->
                {
                    Actor = context.Binding
                    CaseId = caseId
                })

        { new IClaimsCore with
            member _.Describe() = TypedProjection.description clock

            member _.Prepare(request, cancellationToken) =
                prepareForActor store recovery clock commandAuthority request cancellationToken

            member _.Execute(request, cancellationToken) =
                match commandAuthority with
                | Some authority ->
                    TypedSubmission.execute store recovery clock authority request cancellationToken
                | None ->
                    Task.FromResult(
                        SubmissionOutcome.RejectedBeforeAttempt(None, Rejection.ResourceUnavailable)
                    )

            member _.Get(reference, cancellationToken) =
                TypedQueries.get store reference cancellationToken

            member _.List(request, cancellationToken) =
                TypedQueries.list store request cancellationToken

            member _.History(request, cancellationToken) =
                TypedQueries.history store request cancellationToken

            member _.ObserveOperation(operationId, cancellationToken) =
                TypedQueries.observe store operationId cancellationToken

            member _.Recovery = recoveryWorkflow
        }
