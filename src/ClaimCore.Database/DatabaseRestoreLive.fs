namespace ClaimCore.Database

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal RestoredPairFacts =
    {
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        AuthorityRevision: int64
        PrimarySystemId: string
        PrimaryTimeline: int64
        PrimaryWalEndpoint: string
        WitnessSystemId: string
        WitnessTimeline: int64
        WitnessWalEndpoint: string
        WitnessCutoff: int64
        WitnessCutoffHash: string
        PendingIntents: int64
        ManagedCopyCount: int64
        ManagedCopySnapshotSha256: string
        CatalogManifestSha256: string
    }

module internal DatabaseRestoreLive =
    let private systemInfo (connection: NpgsqlConnection) transaction =
        use command =
            new NpgsqlCommand(
                "SELECT s.system_identifier::text,c.timeline_id::bigint,"
                + "(CASE WHEN pg_is_in_recovery() THEN pg_last_wal_replay_lsn() "
                + "ELSE pg_current_wal_flush_lsn() END)::text FROM pg_control_system() s "
                + "CROSS JOIN pg_control_checkpoint() c",
                connection
            )

        transaction |> Option.iter (fun value -> command.Transaction <- value)
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Restored PostgreSQL control data is unavailable."

        let systemId = reader.GetString(0)
        let timeline = reader.GetInt64(1)
        let wal = reader.GetString(2)

        if reader.Read() || timeline < 1L then
            invalidOp "Restored PostgreSQL control data is ambiguous."

        systemId, timeline, wal

    let private installation (connection: NpgsqlConnection) transaction =
        use command =
            new NpgsqlCommand(
                "SELECT installation_id,lineage_id,witness_epoch,writer_generation "
                + "FROM claimcore.installation_lineage WHERE singleton",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Restored installation identity is unavailable."

        let identity =
            reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3)

        if reader.Read() then
            invalidOp "Restored installation identity is ambiguous."

        identity

    let private authorityRevision (connection: NpgsqlConnection) transaction =
        use command =
            new NpgsqlCommand(
                "SELECT revision FROM claimcore.authority_tip WHERE singleton",
                connection,
                transaction
            )

        match command.ExecuteScalar() with
        | :? int64 as value when value > 0L -> value
        | _ -> invalidOp "Restored authority revision is unavailable."

    let private catalogSha () =
        let assembly = typeof<PostgresStore>.Assembly

        match assembly.GetManifestResourceStream("ClaimCore.CatalogManifest.json") with
        | null -> invalidOp "Pinned catalog resource is unavailable."
        | stream ->
            use stream = stream

            if stream.Length > 5000000L then
                invalidOp "Pinned catalog resource exceeds its bound."

            SHA256.HashData(stream) |> Convert.ToHexStringLower

    let private requirePair
        (witness: WitnessProtocol)
        (audit: DataAuditSummary)
        (tip: Snapshot)
        (installationId, lineageId, epoch, writerGeneration)
        primarySystem
        witnessSystem
        allowPendingHandoff
        =
        if
            installationId <> tip.Identity.InstallationId
            || lineageId <> tip.Identity.LineageId
            || epoch <> tip.Identity.Epoch
            || writerGeneration <> tip.WriterGeneration
            || (tip.HandoffPending && not allowPendingHandoff)
            || witness.Identity <> tip.Identity
            || primarySystem = witnessSystem
            || audit.WitnessCutoff <> tip.TipSequence
        then
            invalidOp "Restored primary and witness identities diverge."

    let private inspectWith
        allowPendingHandoff
        (primary: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witnessOwnerConnection: string)
        (witness: WitnessProtocol)
        (audit: DataAuditSummary)
        (tip: Snapshot)
        =
        let installationId, lineageId, epoch, writerGeneration =
            installation primary transaction

        let authority = authorityRevision primary transaction

        let primarySystem, primaryTimeline, primaryWal =
            systemInfo primary (Some transaction)

        let copyCount, inventory = ManagedCopyInventoryDigest.compute primary transaction
        let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection)

        if builder.Username <> "claimcore_witness_owner" then
            invalidOp "Independent witness owner identity is invalid."

        use witnessOwner = new NpgsqlConnection(builder.ConnectionString)
        witnessOwner.Open()
        let witnessSystem, witnessTimeline, witnessWal = systemInfo witnessOwner None

        requirePair
            witness
            audit
            tip
            (installationId, lineageId, epoch, writerGeneration)
            primarySystem
            witnessSystem
            allowPendingHandoff

        {
            InstallationId = installationId
            LineageId = lineageId
            Epoch = epoch
            WriterGeneration = writerGeneration
            AuthorityRevision = authority
            PrimarySystemId = primarySystem
            PrimaryTimeline = primaryTimeline
            PrimaryWalEndpoint = primaryWal
            WitnessSystemId = witnessSystem
            WitnessTimeline = witnessTimeline
            WitnessWalEndpoint = witnessWal
            WitnessCutoff = tip.TipSequence
            WitnessCutoffHash = Convert.ToHexStringLower(tip.TipHash)
            PendingIntents = audit.PendingIntents
            ManagedCopyCount = copyCount
            ManagedCopySnapshotSha256 = inventory
            CatalogManifestSha256 = catalogSha ()
        }

    let inspect primary transaction witnessOwnerConnection witness audit tip =
        inspectWith false primary transaction witnessOwnerConnection witness audit tip

    /// A SETTLE verifier audits the exact pending W1 ticket without opening case work.
    let inspectForHandoff primary transaction witnessOwnerConnection witness audit tip =
        if not tip.HandoffPending then
            invalidOp "A pending W1 handoff is unavailable."

        inspectWith true primary transaction witnessOwnerConnection witness audit tip
