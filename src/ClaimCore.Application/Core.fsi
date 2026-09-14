namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

/// Public typed application boundary. Each endpoint carries its own semantic outcome instead of a
/// catch-all response/status pair; storage and raw preparation records remain inaccessible.
type IClaimsCore =
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

module internal CoreApi =
    val create:
        store: IClaimStore -> recovery: IRecoveryStore -> clock: IBusinessTime -> IClaimsCore
