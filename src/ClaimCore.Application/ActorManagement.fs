namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type GrantTarget =
    | Installation
    | CaseReference of string

[<RequireQualifiedAccess>]
type ActorManagementOutcome =
    | Applied of eventId: Guid * grantRevision: int64 * targetActorId: Guid
    | ResourceUnavailable
    | Unconfirmed of eventId: Guid

/// Called only through a validated actor-bound service session. The caller supplies one stable
/// event ID and exact request; unknown completion is observed/retried with that same identity.
type IActorManagement =
    abstract RegisterActor:
        eventId: Guid * target: PrincipalKey * cancellationToken: CancellationToken ->
            Task<ActorManagementOutcome>

    abstract SetGrant:
        eventId: Guid *
        target: PrincipalKey *
        role: Role *
        scope: GrantTarget *
        active: bool *
        cancellationToken: CancellationToken ->
            Task<ActorManagementOutcome>

    abstract SetEnabled:
        eventId: Guid * target: PrincipalKey * enabled: bool * cancellationToken: CancellationToken ->
            Task<ActorManagementOutcome>

    abstract Observe:
        eventId: Guid * cancellationToken: CancellationToken -> Task<ActorManagementOutcome>
