module ClaimCore.IntegrationTests.ProductExportEventChainTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseErasureArtifactTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.CaseErasurePurgeTests
open ClaimCore.IntegrationTests.FixturePrivateFiles

let private exportId (connection: NpgsqlConnection) caseId =
    use command =
        new NpgsqlCommand(
            "SELECT export_id FROM claimcore.recovery_artifact_exports WHERE case_id=@case",
            connection
        )

    Sql.uuid command "case" caseId

    match command.ExecuteScalar() with
    | :? Guid as value -> value
    | _ -> failtest "Synthetic product export receipt is absent."

let private extraRevision (connection: NpgsqlConnection) (witness: WitnessProtocol) copyId keyId =
    use lookup =
        new NpgsqlCommand(
            "SELECT event_hash FROM claimcore.managed_copies WHERE copy_id=@copy",
            connection
        )

    Sql.uuid lookup "copy" copyId

    let previous =
        match lookup.ExecuteScalar() with
        | :? (byte array) as hash -> hash
        | _ -> failtest "Synthetic product copy projection is absent."

    use command =
        new NpgsqlCommand(
            "INSERT INTO claimcore.managed_copy_events "
            + "(event_id,copy_id,revision,event_kind,producer_kind,canonical_attestation,"
            + "signing_key_id,ed25519_signature,candidate_sha256,previous_hash,event_hash,"
            + "witness_sequence,witness_epoch,witness_entry_hash) VALUES "
            + "(@event,@copy,3,'UNKNOWN','PRODUCT_EXPORT',@canonical,@key,@signature,"
            + "@candidate,@previous,@hash,@sequence,@epoch,@entryHash)",
            connection
        )

    Sql.uuid command "event" (Guid.NewGuid())
    Sql.uuid command "copy" copyId
    Sql.add command "canonical" NpgsqlDbType.Bytea (box [| byte '{'; byte '}'; byte '\n' |])
    Sql.uuid command "key" keyId

    for name in [ "signature"; "candidate"; "hash"; "entryHash" ] do
        let size = if name = "signature" then 64 else 32
        Sql.add command name NpgsqlDbType.Bytea (box (RandomNumberGenerator.GetBytes(size)))

    Sql.add command "previous" NpgsqlDbType.Bytea (box previous)

    Sql.integer
        command
        "sequence"
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence
         + 1000L)

    Sql.integer command "epoch" witness.Identity.Epoch
    Expect.equal (command.ExecuteNonQuery()) 1 "One synthetic extra revision was inserted."

let private refusesExtra
    owner
    _
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    _
    _
    _
    _
    change
    _
    caseId
    =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    let holder = human "export-extra-event-signer"
    grantCustodian runtime proposer holder

    let key, _, keyId, _ =
        registeredSigner runtime proposer holder CopySignerPurpose.CopyAttestor witness connection

    use key = key
    let commitments = syntheticCommitments witness.Identity
    let draft = CaseLifecycleCandidate.draft caseId change

    match
        CaseErasurePurge.execute
            owner
            connection
            witness
            commitments
            (syntheticInventory caseId)
            draft
            CancellationToken.None
        |> await
    with
    | OwnerPurgeOutcome.Purged _ -> ()
    | _ -> failtest "Synthetic export purge prerequisite failed."

    use audit = new NpgsqlConnection(owner)
    audit.Open()

    DataAudit.runWithSuppression audit witness (Some commitments) CancellationToken.None
    |> await
    |> ignore

    extraRevision connection witness (exportId connection caseId) keyId

    Expect.throwsT<InvalidDataException>
        (fun () ->
            DataAudit.runWithSuppression audit witness (Some commitments) CancellationToken.None
            |> await
            |> ignore)
        "Full audit refuses an unlinked PRODUCT_EXPORT revision despite unchanged projection."

let tests =
    testList
        "product export copy event chain"
        [
            testCase
                "[CC-BACKUP-001] extra product-export revision cannot evade global copy audit"
                (fun _ -> withArtifact refusesExtra)
        ]
