namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

/// The only application entry point for CLI, Web, and native callers. Composition supplies narrow
/// claim and recovery ports; no caller receives persistence objects or transition callbacks.
type IClaimsCore =
    abstract Describe: unit -> CoreDescription
    abstract Prepare: CommandDraft * CancellationToken -> Task<PrepareOutcome>
    abstract Execute: CommandDraft * CancellationToken -> Task<SubmissionOutcome>

    abstract Get: string * CancellationToken -> Task<QueryOutcome<Lookup<CurrentCase, string>>>

    abstract List: CaseListRequest * CancellationToken -> Task<QueryOutcome<CaseSummaryPage>>

    abstract History:
        HistoryRequest * CancellationToken -> Task<QueryOutcome<Lookup<HistoryResultPage, string>>>

    abstract ObserveOperation:
        Guid * CancellationToken -> Task<QueryOutcome<Lookup<OperationReceipt, Guid>>>

    abstract Recovery: IRecoveryWorkflow

module internal CoreApi =
    let create
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
        : IClaimsCore =
        BuildIdentity.requireCompatibleAssembly typeof<Claim>.Assembly

        let recoveryWorkflow = RecoveryCoordinator.create store recovery clock

        { new IClaimsCore with
            member _.Describe() = TypedProjection.description clock

            member _.Prepare(draft, cancellationToken) =
                TypedWorkflow.prepare store recovery clock draft cancellationToken

            member _.Execute(draft, cancellationToken) =
                TypedWorkflow.execute store recovery clock draft cancellationToken

            member _.Get(reference, cancellationToken) =
                TypedWorkflow.get store reference cancellationToken

            member _.List(request, cancellationToken) =
                TypedWorkflow.list store request cancellationToken

            member _.History(request, cancellationToken) =
                TypedWorkflow.history store request cancellationToken

            member _.ObserveOperation(operationId, cancellationToken) =
                TypedWorkflow.observe store operationId cancellationToken

            member _.Recovery = recoveryWorkflow
        }
