module internal ClaimCore.IntegrationTests.BackupHealthRuntimeActorRaceFacts

open System.Threading
open System
open System.Data
open System.Security.Cryptography
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerSetup
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence

let reviewedProfile (policyBytes: byte array) : ReviewedDeploymentProfile =
    {
        PublicationRootKey = Array.create 32 7uy
        BackupHealthPolicySha256 = SHA256.HashData(policyBytes) |> Convert.ToHexStringLower
    }

let acceptedHistory owner operationId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.case_changes WHERE operation_id=@operation",
            connection
        )

    Sql.uuid command "operation" operationId

    match command.ExecuteScalar() with
    | :? int64 as count -> count
    | _ -> invalidOp "Synthetic accepted-history readback is unavailable."

[<NoEquality; NoComparison>]
type private CopyFact =
    {
        Id: Guid
        Cluster: string
        Kind: string
        Revision: int64
        SystemId: string
        Timeline: int64
        PhysicalReceiptSha256: string
        VerifiedAt: DateTimeOffset
    }

let private copy (connection: NpgsqlConnection) (value: VerifiedPhysicalCopy) =
    use command =
        new NpgsqlCommand(
            "SELECT cluster_name,copy_kind,revision,postgres_system_id,timeline,"
            + "verification_proof_sha256,last_verified_at "
            + "FROM claimcore.managed_copies WHERE copy_id=@copy AND state='RETAINED'",
            connection
        )

    Sql.uuid command "copy" value.CopyId
    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        invalidOp "A physical health copy is not retained."

    let result =
        {
            Id = value.CopyId
            Cluster = reader.GetString(0)
            Kind = reader.GetString(1)
            Revision = reader.GetInt64(2)
            SystemId = reader.GetString(3)
            Timeline = int64 (reader.GetInt32(4))
            PhysicalReceiptSha256 = reader.GetFieldValue<byte array>(5) |> Convert.ToHexStringLower
            VerifiedAt = reader.GetFieldValue<DateTimeOffset>(6)
        }

    if reader.Read() then
        invalidOp "A physical health copy row is ambiguous."

    result

let private revision (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            "SELECT revision FROM claimcore.authority_tip WHERE singleton",
            connection
        )

    match command.ExecuteScalar() with
    | :? int64 as value -> value
    | _ -> invalidOp "Current health authority revision is absent."

let private signerHolder (connection: NpgsqlConnection) signerKeyId =
    use command =
        new NpgsqlCommand(
            "SELECT holder_actor_id FROM claimcore.managed_copy_signers WHERE signing_key_id=@key",
            connection
        )

    Sql.uuid command "key" signerKeyId

    match command.ExecuteScalar() with
    | :? Guid as holder -> holder
    | _ -> invalidOp "Synthetic CHECKPOINT holder is absent."

let private baseClaim (item: CopyFact) =
    {
        CopyId = item.Id
        Revision = item.Revision
        PhysicalReceiptSha256 = item.PhysicalReceiptSha256
        VerifiedAt = item.VerifiedAt
    }

let private genesisFence: BackupHealthWriterFence =
    {
        Kind = "GENESIS"
        HandoffId = None
        W1Sequence = None
        W1Hash = None
        ActivationSequence = None
        ActivationHash = None
        OldGeneration = None
        NewGeneration = None
    }

let private checkpoint (capture: PhysicalCopyCapture) now : BackupHealthCheckpoint =
    {
        Sequence = capture.Facts.WitnessCutoff
        Hash = capture.Facts.WitnessCutoffHash
        ObjectSha256 = capture.BackupManifestSha256
        VerifiedAt = now
    }

let private testRestore (capture: PhysicalCopyCapture) now : BackupHealthTestRestore =
    {
        ReportSha256 = capture.BackupManifestSha256
        WitnessCutoff = capture.Facts.WitnessCutoff
        WitnessCutoffHash = capture.Facts.WitnessCutoffHash
        VerifiedAt = now
    }

let private claims
    connection
    (snapshot: Snapshot)
    (capture: PhysicalCopyCapture)
    (primaryBase: CopyFact)
    (witnessBase: CopyFact)
    (primaryWal: BackupHealthWal)
    (witnessWal: BackupHealthWal)
    inventory
    now
    signerKeyId
    : BackupHealthClaims =
    {
        InstallationId = snapshot.Identity.InstallationId
        LineageId = snapshot.Identity.LineageId
        Epoch = snapshot.Identity.Epoch
        WriterGeneration = snapshot.WriterGeneration
        PolicyId = "reviewed-test-recovery"
        AuthorityRevision = revision connection
        WitnessTipSequence = snapshot.TipSequence
        WitnessTipHash = Convert.ToHexStringLower(snapshot.TipHash)
        CheckedAt = now
        ValidUntil = now.AddSeconds(90.)
        MaximumBackupAgeSeconds = 3600L
        MaximumWalLagSeconds = 3600L
        MaximumCheckpointAgeSeconds = 3600L
        MaximumRestoreTestAgeSeconds = 3600L
        RestoreHorizonSeconds = 7200L
        PrimarySystemId = primaryBase.SystemId
        PrimaryTimeline = primaryBase.Timeline
        WitnessSystemId = witnessBase.SystemId
        WitnessTimeline = witnessBase.Timeline
        PrimaryBase = baseClaim primaryBase
        WitnessBase = baseClaim witnessBase
        PrimaryWal = primaryWal
        WitnessWal = witnessWal
        Checkpoint = checkpoint capture now
        TestRestore = testRestore capture now
        KnownCopyInventorySha256 = inventory
        ArtifactCutoffSequence = snapshot.TipSequence
        WriterFence = genesisFence
        SignerKeyId = signerKeyId
        SignerHolderActorId = signerHolder connection signerKeyId
    }

let build
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (capture: PhysicalCopyCapture)
    (registered: RegisteredWalCapture)
    (verified: VerifiedPhysicalCopy list)
    signerKeyId
    =
    let copies = verified |> List.map (copy connection)

    let one cluster kind =
        copies
        |> List.filter (fun item -> item.Cluster = cluster && item.Kind = kind)
        |> List.exactlyOne

    let primaryBase = one "PRIMARY" "BASE"
    let witnessBase = one "WITNESS" "BASE"

    let wal now cluster horizon =
        let ids =
            copies
            |> List.filter (fun item -> item.Cluster = cluster && item.Kind = "WAL")
            |> List.map _.Id
            |> List.sort

        {
            CopyIds = ids
            RegisteredHorizon = horizon
            ArchiveInspectionSha256 = String('a', 64)
            VerifiedAt = now
        }

    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

    let _, inventory =
        (ManagedCopyInventoryDigest.compute connection transaction CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    transaction.Rollback()

    let snapshot = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    let now = Sql.databaseNowSync connection Unchecked.defaultof<NpgsqlTransaction>

    claims
        connection
        snapshot
        capture
        primaryBase
        witnessBase
        (wal now "PRIMARY" registered.PrimaryHorizon)
        (wal now "WITNESS" registered.WitnessHorizon)
        inventory
        now
        signerKeyId
