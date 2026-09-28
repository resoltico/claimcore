module internal ClaimCore.IntegrationTests.ManagedCopyAdoptedExternalCase

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyExternalPublicationFixture
open ClaimCore.IntegrationTests.ManagedCopyAdoptedExternalSource
open ClaimCore.IntegrationTests.CaseLifecycleStoreFixture
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.CaseErasurePurgeTests

let private ct = CancellationToken.None

let openPublished owner (witness: WitnessProtocol) (runtime: Runtime) proposer retention =
    let actor = runtime.ForActor proposer

    let input =
        openRequest (Guid.NewGuid()) ("ADOPT-EXT-" + Guid.NewGuid().ToString("N"))

    executeAccepted actor input
    let id = caseId owner input.CaseReference
    let publication = createWithRetention owner witness runtime proposer id retention
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity

    match
        ManagedCopyExternalPublicationOwner.publish
            owner
            witness
            commitments
            publication.PrivateLocation
            publication.Submission
            ct
        |> await
    with
    | ExternalCopyPublicationOutcome.Published _ -> ()
    | _ -> failtest "Synthetic external publication failed."

    let source = published owner publication.CopyId
    actor, input, id, publication, commitments, source

let private applyLifecycle (actor: IActorClaimsCore) reference mutation label =
    let request = change (Guid.NewGuid()) reference (review actor reference) mutation

    match actor.Lifecycle.Apply(request, ct) |> await with
    | LifecycleWriteOutcome.Applied _ -> request
    | _ -> failtest label

let private approvePurge
    (runtime: Runtime)
    first
    second
    (request: LifecycleChange)
    (expires: DateTimeOffset)
    =
    for steward in [ first; second ] do
        match
            (runtime.ForActor steward)
                .Lifecycle.Approve(request, Guid.NewGuid(), expires.AddMinutes(-1.0), ct)
            |> await
        with
        | LifecycleWriteOutcome.Applied _ -> ()
        | _ -> failtest "Synthetic external purge approval failed."

let fenceAndPurge
    owner
    (witness: WitnessProtocol)
    (runtime: Runtime)
    first
    second
    (actor: IActorClaimsCore)
    (input: ClaimCore.Domain.CommandRequest)
    caseId
    commitments
    =
    applyLifecycle
        actor
        input.CaseReference
        (LifecycleMutation.RequestErasure "Synthetic external adoption fence")
        "Synthetic external case fence failed"
    |> ignore

    applyLifecycle
        actor
        input.CaseReference
        (LifecycleMutation.MarkErasurePending "Synthetic copy inventory pending")
        "Synthetic external pending phase failed"
    |> ignore

    let expires = utcMicrosecond (DateTimeOffset.UtcNow.AddHours(1.0))

    let purgeRequest =
        change
            (Guid.NewGuid())
            input.CaseReference
            (review actor input.CaseReference)
            (LifecycleMutation.PurgeLivePayload("Synthetic external copy purge", expires))

    approvePurge runtime first second purgeRequest expires
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    let draft = CaseLifecycleCandidate.draft caseId purgeRequest

    match
        CaseErasurePurge.execute
            owner
            connection
            witness
            commitments
            (syntheticInventory caseId)
            draft
            ct
        |> await
    with
    | OwnerPurgeOutcome.Purged _ -> ()
    | _ -> failtest "Synthetic external live purge failed."
