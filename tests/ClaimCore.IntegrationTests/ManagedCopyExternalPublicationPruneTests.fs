module ClaimCore.IntegrationTests.ManagedCopyExternalPublicationPruneTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence
open ClaimCore.IntegrationTests.ManagedCopyExternalPublicationFixture

let private ct = CancellationToken.None

let private withPrunedPublication action =
    CaseLifecycleStoreFixture.setup
        (fun
            owner
            source
            (witness: WitnessProtocol)
            (runtime: Runtime)
            proposer
            first
            second
            _
            writer ->
            let mutable publication: PublicationFixture option = None

            let beforeRequest _ (input: CommandRequest) =
                let caseId = CaseErasurePurgeTests.caseId owner input.CaseReference
                let prepared = create owner witness runtime proposer caseId
                let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity

                match
                    ManagedCopyExternalPublicationOwner.publish
                        owner
                        witness
                        commitments
                        prepared.PrivateLocation
                        prepared.Submission
                        ct
                    |> await
                with
                | ExternalCopyPublicationOutcome.Published _ -> publication <- Some prepared
                | _ -> failtest "Pre-fence publication failed."

            let fixture =
                prepare beforeRequest owner source witness runtime proposer first second writer

            assertFirstPrune fixture
            fullAudit fixture

            let published =
                publication |> Option.defaultWith (fun () -> failtest "Publication missing")

            match
                ManagedCopyExternalPublicationOwner.publish
                    owner
                    witness
                    fixture.Commitments
                    published.PrivateLocation
                    published.Submission
                    ct
                |> await
            with
            | ExternalCopyPublicationOutcome.Published(id, _) when
                id = published.Submission.PublicationId
                ->
                ()
            | _ -> failtest "Exact postprune publication retry failed."

            action fixture published)

let private auditRefuses (fixture: PruneFixture) =
    use connection = new NpgsqlConnection(fixture.Owner)
    connection.Open()

    Expect.throws
        (fun () ->
            DataAudit.runWithSuppression connection fixture.Witness (Some fixture.Commitments) ct
            |> await
            |> ignore)
        "Postprune full audit must reject changed external-publication provenance"

let private missingReceipt =
    testCase
        "[CC-AUDIT-001] pruned publication intent still requires immutable primary receipt"
        (fun _ ->
            withPrunedPublication (fun fixture publication ->
                use connection = new NpgsqlConnection(fixture.Owner)
                connection.Open()

                use command =
                    new NpgsqlCommand(
                        "DELETE FROM claimcore.managed_copy_external_publications "
                        + "WHERE publication_id=@publication",
                        connection
                    )

                Sql.uuid command "publication" publication.Submission.PublicationId
                Expect.equal (command.ExecuteNonQuery()) 1 "One synthetic receipt was removed"
                auditRefuses fixture))

let private alteredMarker =
    testCase
        "[CC-AUDIT-001] changed postprune publication marker breaks signed target digest"
        (fun _ ->
            withPrunedPublication (fun fixture publication ->
                use connection = new NpgsqlConnection(fixture.Owner)
                connection.Open()

                use command =
                    new NpgsqlCommand(
                        "UPDATE claimcore.case_erasure_prune_targets "
                        + "SET is_external_publication=false "
                        + "WHERE case_id=@case AND operation_id=@publication "
                        + "AND phase='SETTLED_AUTHORITY'",
                        connection
                    )

                Sql.uuid command "case" fixture.CaseId
                Sql.uuid command "publication" publication.Submission.PublicationId
                Expect.equal (command.ExecuteNonQuery()) 1 "One signed target marker was changed"
                auditRefuses fixture))

let tests =
    testList "postprune external publication audit" [ missingReceipt; alteredMarker ]
