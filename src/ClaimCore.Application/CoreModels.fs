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
type QueryOutcome<'value> =
    | Succeeded of 'value
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled

[<RequireQualifiedAccess>]
type PreviewOutcome =
    | Reviewed of review: AdvisoryReview
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
    abstract Context: CancellationToken -> Task<QueryOutcome<ActorContext>>
    abstract Preview: CaseRequest * CancellationToken -> Task<PreviewOutcome>
    abstract Execute: CaseRequest * CancellationToken -> Task<ExecuteOutcome>
    abstract Get: caseReference: string * CancellationToken -> Task<QueryOutcome<CaseReadModel>>
    abstract List: PageRequest * CancellationToken -> Task<QueryOutcome<CaseSummaryPage>>
    abstract History: HistoryRequest * CancellationToken -> Task<QueryOutcome<HistoryResultPage>>
    abstract Observe: operationId: Guid * CancellationToken -> Task<QueryOutcome<OperationReceipt>>

    abstract ExecuteAuthority:
        ClaimCore.Contract.Protocol.AuthorityRequest * CancellationToken -> Task<ExecuteOutcome>

    abstract ListActors: PageRequest * CancellationToken -> Task<QueryOutcome<ActorReviewPage>>

    abstract ListGrants: GrantsRequest * CancellationToken -> Task<QueryOutcome<AuthorityGrantPage>>
