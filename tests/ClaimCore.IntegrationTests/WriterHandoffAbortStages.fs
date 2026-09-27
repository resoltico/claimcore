module internal ClaimCore.IntegrationTests.WriterHandoffAbortStages

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.FixturePrivateFiles
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.WriterHandoffAbortScenario
open ClaimCore.IntegrationTests.WriterHandoffAbortProcessFixture
open ClaimCore.IntegrationTests.WriterHandoffAbortRetryAssertions
open ClaimCore.IntegrationTests.WriterHandoffProtocolAssertions
open ClaimCore.IntegrationTests.WriterHandoffProtocolPreparation

[<NoEquality; NoComparison>]
type Evidence =
    {
        Scenario: Scenario
        Context: PreparedSyntheticHandoff
        Value: WriterHandoffAbort
        Source: NpgsqlDataSource
        Canonical: byte array
        SignatureOne: byte array
        SignatureTwo: byte array
    }

let private actorFacts (connection: NpgsqlConnection) principal =
    let kind, issuer, stable = PrincipalKey.storageParts principal

    use command =
        new NpgsqlCommand(
            "SELECT a.actor_id,g.changed_revision FROM claimcore.actors a "
            + "JOIN claimcore.actor_grants g ON g.actor_id=a.actor_id "
            + "WHERE a.principal_kind=@kind AND a.issuer=@issuer AND a.principal_value=@value "
            + "AND g.scope_kind='INSTALLATION' AND g.role_name='OWNER' AND g.active",
            connection
        )

    command.Parameters.AddWithValue("kind", kind) |> ignore
    command.Parameters.AddWithValue("issuer", issuer) |> ignore
    command.Parameters.AddWithValue("value", stable) |> ignore
    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failtest "Synthetic owner actor grant is absent."

    let result = reader.GetGuid(0), reader.GetInt64(1)
    Expect.isFalse (reader.Read()) "Synthetic owner grant is unique."
    result

let private assertAudit (scenario: Scenario) pending aborts =
    use source = RuntimeDataSource.create scenario.App
    use audit = RuntimeDatabase.openConnection source
    let summary = DataAudit.run audit scenario.Witness CancellationToken.None |> await
    Expect.equal summary.PendingIntents pending "Abort stage has exact quarantine count."
    Expect.equal summary.WriterHandoffAborts aborts "Abort receipt is fully replayed."

let prepare (scenario: Scenario) (source: NpgsqlDataSource) =
    let context =
        prepareHandoff
            scenario.Runtime
            scenario.Witness
            scenario.Primary
            scenario.Owner
            scenario.App
            scenario.Writer
            scenario.First
            scenario.Second
            scenario.CheckpointId
            scenario.CheckpointKey
            scenario.CheckpointAlgorithm
            scenario.OldCapability
            scenario.NewCapability

    let canonical =
        draftCandidate
            scenario.Owner
            scenario.App
            scenario.Writer
            scenario.Witness
            context.OwnerWitness
            context.Value.HandoffId
            scenario.KeyOneId
            scenario.KeyTwoId

    let value = WriterHandoffAbort.parse canonical |> Option.get
    let actorOne, grantOne = actorFacts scenario.Primary scenario.First
    let actorTwo, grantTwo = actorFacts scenario.Primary scenario.Second

    Expect.equal
        (value.OwnerOneActorId, value.OwnerOneGrantRevision)
        (actorOne, grantOne)
        "Draft derives first registered owner authority."

    Expect.equal
        (value.OwnerTwoActorId, value.OwnerTwoGrantRevision)
        (actorTwo, grantTwo)
        "Draft derives second registered owner authority."

    Expect.equal value.PrepareSequence context.Ticket.Sequence "Draft binds exact pending W1."

    {
        Scenario = scenario
        Context = context
        Value = value
        Source = source
        Canonical = canonical
        SignatureOne = scenario.Algorithm.Sign(scenario.KeyOne, canonical)
        SignatureTwo = scenario.Algorithm.Sign(scenario.KeyTwo, canonical)
    }

let retainPending (evidence: Evidence) =
    let scenario = evidence.Scenario

    match
        WriterHandoffOwnerAbortStage.start
            scenario.Primary
            evidence.Source
            evidence.Context.OwnerWitness
            scenario.Witness
            (Some(syntheticCommitments scenario.Witness.Identity))
            evidence.Canonical
            evidence.SignatureOne
            evidence.SignatureTwo
            scenario.OldCapability
        |> await
    with
    | WriterHandoffAbortOutcome.AwaitingPrimary(id, _, _) when id = evidence.Value.HandoffId -> ()
    | _ -> failtest "A1 did not retain the pending fence."

    assertA1Retry
        evidence.Context
        scenario.Primary
        evidence.Source
        scenario.Witness
        evidence.Value
        evidence.Canonical
        evidence.SignatureOne
        evidence.SignatureTwo
        scenario.OldCapability

    Expect.isTrue (scenario.Witness.Snapshot().HandoffPending) "A1 leaves witness quarantined."
    assertAudit scenario 1L 0L

    assertMissingA2
        evidence.Context
        scenario.Primary
        evidence.Source
        scenario.Witness
        evidence.Canonical
        evidence.SignatureOne
        evidence.SignatureTwo
        scenario.OldCapability

let commitPrimary (evidence: Evidence) =
    let scenario = evidence.Scenario

    match
        WriterHandoffOwnerAbortBackfill.commit
            scenario.Primary
            scenario.Witness
            evidence.Canonical
            evidence.SignatureOne
            evidence.SignatureTwo
        |> await
    with
    | WriterHandoffAbortOutcome.AwaitingRelease(id, _, _) when id = evidence.Value.HandoffId -> ()
    | _ -> failtest "A2 did not co-commit primary receipt."

    assertAudit scenario 1L 1L

let releasePair (evidence: Evidence) =
    let scenario = evidence.Scenario

    match
        WriterHandoffOwnerAbortRelease.release
            scenario.Primary
            evidence.Source
            evidence.Context.OwnerWitness
            scenario.Witness
            (Some(syntheticCommitments scenario.Witness.Identity))
            evidence.Canonical
            evidence.SignatureOne
            evidence.SignatureTwo
            scenario.OldCapability
        |> await
    with
    | WriterHandoffAbortOutcome.Released(id, _, _) when id = evidence.Value.HandoffId -> ()
    | _ -> failtest "A3 did not release the exact pair."

    assertAudit scenario 0L 1L

let assertRestored (evidence: Evidence) =
    let scenario = evidence.Scenario

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            scenario.Runtime.ForActor(scenario.First).Definition(CancellationToken.None)
            |> await
            |> ignore)
        "The old already-open runtime cannot resurrect after abort release."

    use reopened = openRuntime scenario.App scenario.Writer

    reopened.ForActor(scenario.First).Definition(CancellationToken.None)
    |> await
    |> ignore

    let later = Guid.NewGuid()

    (reopened.ForActor scenario.First)
        .Management.SetGrant(
            later,
            scenario.CheckpointHolder,
            Role.CaseReader,
            GrantTarget.Installation,
            true,
            CancellationToken.None
        )
    |> await
    |> appliedManagement later

    verifyExactRetry
        scenario.Owner
        scenario.App
        scenario.Writer
        scenario.Witness
        evidence.Context.OwnerWitness
        evidence.Canonical
        evidence.SignatureOne
        evidence.SignatureTwo
