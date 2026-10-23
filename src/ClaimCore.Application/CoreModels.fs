namespace ClaimCore.Application

open ClaimCore.Contract

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Admission vocabulary shared by persistence and the composition root.
[<RequireQualifiedAccess>]
type RuntimeOpenFault =
    | InvalidTransport
    | UnsupportedServer
    | UnsafeRole
    | SchemaMismatch
    | InstallationInvalid
    | HeadMismatch
    | Unavailable
    | Cancelled

[<RequireQualifiedAccess>]
type AdmittedAuthorityAct =
    | RegisterActor
    | SetGrant of role: Role * scope: GrantTarget * active: bool
    | SetEnabled of enabled: bool

type AdmittedAuthorityRequest =
    {
        OperationId: Guid
        Target: PrincipalKey
        Act: AdmittedAuthorityAct
    }

[<RequireQualifiedAccess>]
type PreviewOutcome =
    | Reviewed of review: OperationReview
    | AlreadyAccepted of OperationReceipt
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled

[<RequireQualifiedAccess>]
type ExecuteOutcome =
    | Accepted of OperationReceipt
    | AlreadyAccepted of OperationReceipt
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled
    | OutcomeUnknown of operationId: Guid

type IActorClaimsCore =
    abstract Context: CancellationToken -> Task<ReadReply<ActorContext>>
    abstract Preview: OperationInput * CancellationToken -> Task<PreviewOutcome>
    abstract Execute: OperationInput * CancellationToken -> Task<ExecuteOutcome>
    abstract Get: CaseGetRequest * CancellationToken -> Task<ReadReply<CaseReadModel>>
    abstract List: PageRequest * CancellationToken -> Task<ReadReply<CaseSummaryPage>>
    abstract History: HistoryRequest * CancellationToken -> Task<ReadReply<HistoryResultPage>>
    abstract Observe: ObserveRequest * CancellationToken -> Task<ReadReply<OperationReceipt>>

    abstract ListActors: PageRequest * CancellationToken -> Task<ReadReply<ActorReviewPage>>

    abstract ListGrants: GrantsRequest * CancellationToken -> Task<ReadReply<AuthorityGrantPage>>
