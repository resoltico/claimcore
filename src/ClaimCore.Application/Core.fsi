namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

/// Public typed application boundary. Each endpoint carries its own semantic outcome instead of a
/// catch-all response/status pair; storage and raw preparation records remain inaccessible.
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

/// Authenticated service-facing core. Principal acquisition is explicit in Hosting; every
/// endpoint performs its own current grant check, including definition discovery.
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
    val createActor:
        store: IClaimStore ->
        recovery: IRecoveryStore ->
        clock: IBusinessTime ->
        context: ActorCallContext ->
        artifactAuthority: IRecoveryArtifactAuthority ->
            IClaimsCore
