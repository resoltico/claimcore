namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Immutable installation configuration read by installation checks.
type Installation =
    {
        InstallationId: Guid
        BusinessTimeZone: string
    }


type HousekeepingReport =
    {
        ObservedAt: DateTimeOffset
        SessionsDeleted: int64
        AccessRequestsDeleted: int64
    }


/// A bounded diagnostic value. It deliberately has no exception, message, SQL or user-data field.
[<RequireQualifiedAccess>]
type FailurePhase =
    | Open
    | Read
    | Head
    | Append
    | Projection
    | Commit
    | Session
    | Housekeeping
    | Cleanup

[<RequireQualifiedAccess>]
type FailureClass =
    | Cancelled
    | Timeout
    | Provider
    | InputOutput
    | Installation
    | Internal

[<NoEquality; NoComparison>]
type FailureDiagnostic =
    {
        Correlation: Guid
        RequestCorrelation: Guid option
        Phase: FailurePhase
        Classification: FailureClass
        Methods: string array
    }


/// A read snapshot, not a history verification or an all-process drain assertion.
type InstallationStatus =
    {
        InstallationId: Guid
        Baseline: string
        BaselineSha256: string
        BusinessTimeZone: string
        ObservedAt: DateTimeOffset
        Position: LedgerPosition
        Maintenance: RegisterMaintenance
        RecoveryPublicationGeneration: int64
        InstallationFormat: int
        InitialOwner: ActorRef option
        Actors: int64
        Cases: int64
        WorkRecords: int64
        Reminders: int64
        Sessions: int64
        ExpiredSessions: int64
    }


[<RequireQualifiedAccess>]
type SessionEndReason =
    | Logout
    | Expired
    | Invalid

/// Committed operational changes are returned to the host that owns session telemetry.
type SessionChanges<'value> =
    {
        Value: 'value
        Ended: SessionEndReason list
    }

module internal SessionChanges =
    let unchanged value = { Value = value; Ended = [] }

type HumanAuthentication =
    {
        IssuedAt: DateTimeOffset
        AuthenticatedAt: DateTimeOffset
    }

[<RequireQualifiedAccess>]
type BrowserCredential =
    | StatusOnly
    | Protected of
        installation: Guid *
        recoveryGeneration: Guid *
        actor: ActorId *
        generation: Sequence

/// The transport supplies these facts only after validating its own credential.
[<RequireQualifiedAccess>]
type VerifiedCredential =
    | Browser of BrowserCredential
    | HumanBearer of HumanAuthentication
    | MachineBearer of issuedAt: DateTimeOffset

module internal CredentialFreshness =
    // Issuers are admitted only with synchronized clocks and this maximum possible lead.
    let maximumLead = TimeSpan.FromSeconds 2.

    let after (observed: DateTimeOffset) (cutoff: DateTimeOffset) (issued: DateTimeOffset) =
        cutoff <= observed
        && issued - cutoff > maximumLead
        && issued - observed <= maximumLead

    let human observed cutoff (proof: HumanAuthentication) =
        proof.AuthenticatedAt <= proof.IssuedAt
        && after observed cutoff proof.IssuedAt
        && after observed cutoff proof.AuthenticatedAt

/// Principal metadata is checked against the protected ticket by the cookie adapter.
type StoredSessionTicket =
    {
        PrincipalKey: string
        Payload: byte array
    }

/// Operational browser flows and tickets use locally committed ordering evidence.
type ISessionStore =
    abstract StartFlow: CancellationToken -> Task<Guid>

    abstract Create:
        PrincipalKey *
        (Guid * HumanAuthentication) option *
        (BrowserCredential -> byte array) *
        DateTimeOffset option *
        TimeSpan *
        TimeSpan *
        CancellationToken ->
            Task<SessionChanges<string>>

    abstract Retrieve:
        string * CancellationToken -> Task<SessionChanges<StoredSessionTicket option>>

    abstract Renew:
        string * PrincipalKey * byte array * (byte array -> bool) * CancellationToken ->
            Task<SessionChanges<unit>>

    abstract Remove: string * SessionEndReason * CancellationToken -> Task<SessionChanges<unit>>

type InitialOwner =
    {
        Principal: PrincipalKey
        Profile: ActorProfileInput
        Roles: Role list
    }


type OwnerRecoveryInput =
    {
        Principal: PrincipalKey
        ActorId: ActorId option
        Name: string option
    }

[<RequireQualifiedAccess>]
type OwnerRecoveryResult =
    | Recovered of ActorId * OperationId
    | Unchanged of ActorId

[<RequireQualifiedAccess>]
type AdministrationFailure =
    | Fault of OperatorFault
    | OutcomeUnknown
