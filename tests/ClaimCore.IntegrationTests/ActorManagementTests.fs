module ClaimCore.IntegrationTests.ActorManagementTests

open System
open System.Data
open System.Security.Cryptography
open System.Text
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private openRuntime app writer =
    Runtime.OpenPostgres(
        app,
        writer,
        witnessKey (),
        suppressionKeyFile (),
        artifactKeyRingFile (),
        CancellationToken.None
    )
    |> await
    |> accepted

let private applied eventId =
    function
    | ActorManagementOutcome.Applied(id, revision, actorId) when
        id = eventId && revision > 0L && actorId <> Guid.Empty
        ->
        revision, actorId
    | _ -> failtest "Authenticated management mutation was not witnessed."

let private registerReplay (management: IActorManagement) target =
    let eventId = Guid.NewGuid()

    let register value =
        management.RegisterActor(eventId, value, CancellationToken.None) |> await

    let original = register target |> applied eventId
    let replayed = register target |> applied eventId
    Expect.equal replayed original "Exact registration retry observes one original event."

    Expect.equal
        (register (human "changed-target"))
        ActorManagementOutcome.ResourceUnavailable
        "Changed content under one event ID cannot replace the first actor."

let private grantReplay (management: IActorManagement) target =
    let eventId = Guid.NewGuid()

    let grant () =
        management.SetGrant(
            eventId,
            target,
            Role.DataSteward,
            GrantTarget.Installation,
            true,
            CancellationToken.None
        )
        |> await

    let original = grant () |> applied eventId
    let replayed = grant () |> applied eventId
    Expect.equal replayed original "Grant retry never advances a second revision."

    match management.Observe(eventId, CancellationToken.None) |> await with
    | ActorManagementOutcome.Applied(_, revision, actorId) ->
        Expect.equal (revision, actorId) original "Observation is bound to original event."
    | _ -> failtest "Owner must observe the exact grant event."

let private retainUnsettledRegistration
    (app: string)
    (witness: WitnessProtocol)
    (principal: PrincipalKey)
    (target: PrincipalKey)
    =
    use source = RuntimeDataSource.create app
    use connection = RuntimeDatabase.openConnection source
    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

    let revision =
        ActorGrantRead.lockRevision connection transaction true CancellationToken.None
        |> await

    let owner =
        ActorGrantRead.loadUnderLock
            connection
            transaction
            principal
            ResourceScope.Installation
            revision
            CancellationToken.None
        |> await
        |> Option.defaultWith (fun () -> failtest "Synthetic owner is absent.")

    let action: ActorAuthorityAction =
        {
            EventId = Guid.NewGuid()
            Revision = revision + 1L
            ActionName = "REGISTER_ACTOR"
            TargetActorId = Guid.NewGuid()
            ApproverActorId = Some owner.ActorId
            Principal = Some target
            Grant = None
            Enabled = Some true
        }

    let canonical = ActorGrantCandidate.encode action

    try
        let intent = witness.BeginAuthority(action.EventId, canonical, None)

        ActorGrantWrite.insertActor
            connection
            transaction
            action.TargetActorId
            target
            action.Revision
        |> await

        ActorGrantWrite.persistEvent connection transaction action canonical intent
        |> await

        transaction.Commit()
        action.EventId, action.Revision, action.TargetActorId
    finally
        CryptographicOperations.ZeroMemory(canonical)

let private pendingSettlementObservation
    (management: IActorManagement)
    (app: string)
    (witness: WitnessProtocol)
    (principal: PrincipalKey)
    =
    let eventId, revision, targetId =
        retainUnsettledRegistration app witness principal (human "pending-owner-target")

    Expect.isNone
        (witness.EvidenceStore.TryReadEvidence(eventId, SettledAuthority))
        "Primary authority committed while W1 response is absent."

    let observed = management.Observe(eventId, CancellationToken.None) |> await

    Expect.equal
        observed
        (ActorManagementOutcome.Applied(eventId, revision, targetId))
        "Observation settles exact committed authority."

    let tip = witness.Snapshot().TipSequence
    let replayed = management.Observe(eventId, CancellationToken.None) |> await
    Expect.equal replayed observed "Exact observation keeps the original authority identity."
    Expect.equal (witness.Snapshot().TipSequence) tip "Observation replay appends no new W1."

let private exactRetry =
    testCase "[CC-AUTH-001] owner management exact retry keeps event and target identity" (fun _ ->
        withAuthorityRuntimeDatabase (fun owner app writer witness ->
            let principal = human "management-owner"
            let target = human "management-steward"
            provision owner witness principal |> ActorGrantTestSupport.applied
            use runtime = openRuntime app writer
            let management = (runtime.ForActor principal).Management
            registerReplay management target
            grantReplay management target
            pendingSettlementObservation management app witness principal

            use source = RuntimeDataSource.create app

            match ActorGrantDeployment.verifyRoster source |> await with
            | DualControlRoster.Ready _ -> ()
            | DualControlRoster.Missing -> failtest "Distinct human steward roster is required."))

let private deniedManagement =
    testCase "[CC-AUTH-001] management denies unknown actor and service stewardship" (fun _ ->
        withAuthorityRuntimeDatabase (fun owner app writer witness ->
            let principal = human "owner-for-denial"
            provision owner witness principal |> ActorGrantTestSupport.applied
            use runtime = openRuntime app writer
            let unknown = (runtime.ForActor(human "unknown-owner")).Management

            Expect.equal
                (unknown.RegisterActor(Guid.NewGuid(), human "target", CancellationToken.None)
                 |> await)
                ActorManagementOutcome.ResourceUnavailable
                "Unknown caller cannot register principals."

            let management = (runtime.ForActor principal).Management
            let automation = service "automation-client"
            let eventId = Guid.NewGuid()

            management.RegisterActor(eventId, automation, CancellationToken.None)
            |> await
            |> applied eventId
            |> ignore

            Expect.equal
                (management.SetGrant(
                    Guid.NewGuid(),
                    automation,
                    Role.Owner,
                    GrantTarget.Installation,
                    true,
                    CancellationToken.None
                 )
                 |> await)
                ActorManagementOutcome.ResourceUnavailable
                "Service principal cannot become human owner."))

let private orphanIntent =
    testCase "[CC-AUTH-001] orphan grant intent remains unknown on exact retry" (fun _ ->
        withAuthorityRuntimeDatabase (fun owner app writer witness ->
            let principal = human "orphan-owner"
            provision owner witness principal |> ActorGrantTestSupport.applied
            let eventId = Guid.NewGuid()
            let bytes = Encoding.UTF8.GetBytes("synthetic-uncommitted-authority-intent")
            witness.BeginAuthority(eventId, bytes, None) |> ignore
            use runtime = openRuntime app writer
            let target = human "orphan-target"
            let management = (runtime.ForActor principal).Management

            Expect.equal
                (management.RegisterActor(eventId, target, CancellationToken.None) |> await)
                (ActorManagementOutcome.Unconfirmed eventId)
                "An intent without primary proof is not retried as new authority."

            Expect.equal
                (management.Observe(eventId, CancellationToken.None) |> await)
                (ActorManagementOutcome.Unconfirmed eventId)
                "Owner observation preserves unknown completion."))

let tests =
    testList "actor management" [ exactRetry; deniedManagement; orphanIntent ]
