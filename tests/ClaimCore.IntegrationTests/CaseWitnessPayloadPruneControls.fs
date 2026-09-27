module internal ClaimCore.IntegrationTests.CaseWitnessPayloadPruneControls

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence

let private ct = CancellationToken.None

let review (fixture: PruneFixture) =
    let status =
        match fixture.Steward.Tombstones.Review(fixture.CaseId, ct) |> await with
        | TombstoneReviewOutcome.Available value -> value
        | _ -> failtest "Postprune steward status is unavailable."

    Expect.isTrue status.WitnessPayloadPruned "Only witness ciphertext is reported pruned"
    Expect.isTrue status.ManagedCopyCertificationPending "Managed-copy absence remains pending"
    Expect.equal status.PrivacyPhase PrivacyPhase.ErasurePending "No final phase"
    status

let private recordHold (fixture: PruneFixture) (status: TombstoneReview) holdId =
    let eventId = Guid.NewGuid()

    let hold =
        {
            EventId = eventId
            CaseId = fixture.CaseId
            ExpectedAuthorityRevision = status.AuthorityRevision
            ExpectedAuthorityHash = status.AuthorityHash
            Mutation =
                TombstoneHoldMutation.Record(
                    holdId,
                    "LEGAL_RETENTION",
                    DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
                )
        }

    match fixture.Steward.Tombstones.ChangeHold(hold, ct) |> await with
    | TombstoneWriteOutcome.Applied(id, _) when id = eventId -> ()
    | _ -> failtest "Postprune witnessed hold did not apply."

let private releaseHold (fixture: PruneFixture) holdId =
    let status = review fixture
    let eventId = Guid.NewGuid()

    let release =
        {
            EventId = eventId
            CaseId = fixture.CaseId
            ExpectedAuthorityRevision = status.AuthorityRevision
            ExpectedAuthorityHash = status.AuthorityHash
            Mutation = TombstoneHoldMutation.Release(holdId, "LEGAL_RELEASE")
        }

    match fixture.Steward.Tombstones.ChangeHold(release, ct) |> await with
    | TombstoneWriteOutcome.Applied(id, _) when id = eventId -> ()
    | _ -> failtest "Postprune witnessed hold release failed."

let holdReleaseAndRetry fixture status =
    let holdId = Guid.NewGuid()
    recordHold fixture status holdId
    fullAudit fixture
    assertExactRetry fixture
    releaseHold fixture holdId
    assertExactRetry fixture

let private targetPrivileges (connection: NpgsqlConnection) (fixture: PruneFixture) =
    use targetRead =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.case_erasure_prune_targets WHERE case_id=@case",
            connection
        )

    Sql.uuid targetRead "case" fixture.CaseId

    Expect.equal
        (targetRead.ExecuteScalar() :?> int64)
        fixture.Action.TargetCount
        "App audits target rows"

    for privilege in [ "INSERT"; "UPDATE"; "DELETE"; "TRUNCATE" ] do
        use denied =
            new NpgsqlCommand(
                "SELECT has_table_privilege(current_user,'claimcore.case_erasure_prune_targets',@privilege)",
                connection
            )

        Sql.text denied "privilege" privilege
        Expect.isFalse (denied.ExecuteScalar() :?> bool) "App cannot alter prune proof"

let assertReadOnlyProof fixture =
    use connection = new NpgsqlConnection(fixture.App)
    connection.Open()
    targetPrivileges connection fixture

    use mutation =
        new NpgsqlCommand(
            "UPDATE claimcore.case_erasure_prune_targets "
            + "SET payload_sha256=payload_sha256 WHERE false",
            connection
        )

    try
        mutation.ExecuteNonQuery() |> ignore
        failtest "App UPDATE unexpectedly altered or admitted prune proof"
    with :? PostgresException as error ->
        Expect.equal error.SqlState "42501" "App mutation is denied by PostgreSQL ACL"

    use approvalRead =
        new NpgsqlCommand(
            "SELECT has_table_privilege(current_user,"
            + "'claimcore.case_erasure_purge_approvals','SELECT')",
            connection
        )

    Expect.isTrue
        (approvalRead.ExecuteScalar() :?> bool)
        "Runtime app role needs read-only purge approval evidence for full audit"

let assertRuntimeIsolation (fixture: PruneFixture) ungranted =
    use reopened =
        match
            Runtime.OpenPostgres(
                fixture.App,
                fixture.Writer,
                witnessKey (),
                suppressionKeyFile (),
                artifactKeyRingFile (),
                ct
            )
            |> await
        with
        | Ok value -> value
        | Error _ -> failtest "Postprune runtime admission refused."

    match
        (reopened.ForActor fixture.StewardPrincipal).Tombstones.Review(fixture.CaseId, ct)
        |> await
    with
    | TombstoneReviewOutcome.Available value when value.WitnessPayloadPruned -> ()
    | _ -> failtest "Reopened runtime did not admit postprune proof."

    let inaccessible =
        (fixture.Runtime.ForActor ungranted).Tombstones.Review(fixture.CaseId, ct)
        |> await

    let absent = fixture.Steward.Tombstones.Review(Guid.NewGuid(), ct) |> await
    Expect.equal inaccessible TombstoneReviewOutcome.ResourceUnavailable "No case disclosure"
    Expect.equal absent TombstoneReviewOutcome.ResourceUnavailable "Missing and inaccessible agree"

    match fixture.Actor.Get(fixture.Input.CaseReference, ct) |> await with
    | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
    | _ -> failtest "Pruned CASE ciphertext reopened ordinary case access"
