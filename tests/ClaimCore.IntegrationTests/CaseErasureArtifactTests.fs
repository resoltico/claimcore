module ClaimCore.IntegrationTests.CaseErasureArtifactTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.CaseErasurePurgeTests

let private grantRecovery source witness principal role =
    let registry = ActorGrantRegistry(source, witness)

    registry.SetGrant(
        principal,
        actorId (ActorGrantStore(source)) principal,
        {
            Role = role
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> applied

let private issueArtifact (actor: IActorClaimsCore) (input: CommandRequest) =
    let digest =
        input |> RequestRecord.encode |> SHA256.HashData |> Convert.ToHexStringLower

    match
        actor.Recovery.ExportEnvelope(input.OperationId, digest, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact) -> artifact.Bytes
    | _ -> failtest "Synthetic old recovery artifact was not issued"

let internal withArtifact action =
    CaseLifecycleStoreFixture.setup
        (fun owner (source, _) witness runtime proposer first second _ writer ->
            grantRecovery source witness proposer Role.RecoveryExporter
            grantRecovery source witness proposer Role.RecoveryOperator
            let mutable artifact: byte array option = None

            let actor, input, change =
                proposalWith
                    (fun current request -> artifact <- Some(issueArtifact current request))
                    runtime
                    proposer
                    first
                    second

            let bytes = artifact |> Option.defaultWith (fun () -> failtest "Artifact missing")
            let id = caseId owner input.CaseReference
            action owner writer witness runtime proposer first second actor input change bytes id)

let internal purgedExportAudit owner witness commitments id (connection: NpgsqlConnection) =
    use audit = new NpgsqlConnection(owner)
    audit.Open()

    let summary =
        DataAudit.runWithSuppression audit witness (Some commitments) CancellationToken.None
        |> await

    Expect.equal summary.ErasureFences 1L "Purged export receipt remains audited"

    use payload =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.recovery_artifact_payloads p "
            + "JOIN claimcore.recovery_artifact_exports e ON e.export_id=p.export_id "
            + "WHERE e.case_id=@case",
            connection
        )

    Sql.uuid payload "case" id
    Expect.equal (payload.ExecuteScalar() :?> int64) 0L "Live export bytes were removed"

let internal oldArtifactDenied (actor: IActorClaimsCore) (bytes: byte array) =
    match actor.Recovery.PreviewEnvelopeImport(bytes, CancellationToken.None) |> await with
    | RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable -> ()
    | _ -> failtest "Old signed export disclosed a purged case"

    let digest = SHA256.HashData(bytes) |> Convert.ToHexStringLower

    match
        actor.Recovery.RetainEnvelopeImport(bytes, digest, CancellationToken.None)
        |> await
    with
    | RecoveryImportRetainOutcome.ImportRejected RecoveryRejection.ResourceUnavailable -> ()
    | _ -> failtest "Old signed export resurrected operation authority"

let private purgeArtifact owner (witness: WitnessProtocol) change id =
    let draft = CaseLifecycleCandidate.draft id change
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    match
        CaseErasurePurge.execute
            owner
            connection
            witness
            commitments
            (syntheticInventory id)
            draft
            CancellationToken.None
        |> await
    with
    | OwnerPurgeOutcome.Purged _ -> commitments
    | _ -> failtest "Synthetic live purge with export failed."

let private syntheticArtifact
    owner
    _
    (witness: WitnessProtocol)
    _
    _
    _
    _
    actor
    _input
    change
    bytes
    id
    =
    let commitments = purgeArtifact owner witness change id
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    purgedExportAudit owner witness commitments id connection
    oldArtifactDenied actor bytes

let private tamperedExportLength owner _ (witness: WitnessProtocol) _ _ _ _ _ _ change _ id =
    let commitments = purgeArtifact owner witness change id
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use alter =
        new NpgsqlCommand(
            "UPDATE claimcore.managed_copies SET ciphertext_bytes=ciphertext_bytes+1 "
            + "WHERE producer_kind='PRODUCT_EXPORT' AND source_case_id=@case",
            connection
        )

    Sql.uuid alter "case" id
    Expect.equal (alter.ExecuteNonQuery()) 1 "One isolated export length was changed"

    Expect.throws
        (fun () ->
            DataAudit.runWithSuppression
                connection
                witness
                (Some commitments)
                CancellationToken.None
            |> await
            |> ignore)
        "A changed postprune length without a changed witnessed export is quarantined"

let private staleSignedArtifact =
    testCase
        "[CC-ERASE-001] signed old recovery artifact cannot revive a live-purged case"
        (fun _ -> withArtifact syntheticArtifact)

let tests =
    testList
        "case erasure old artifact"
        [
            staleSignedArtifact
            testCase
                "[CC-AUDIT-001] postprune export length remains bound to witnessed evidence"
                (fun _ -> withArtifact tamperedExportLength)
        ]
