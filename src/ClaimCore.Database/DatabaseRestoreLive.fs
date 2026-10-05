namespace ClaimCore.Database

open System.Threading
open System
open System.Globalization
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
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT s.system_identifier::text,c.timeline_id::bigint,"
                    + "(CASE WHEN pg_is_in_recovery() THEN pg_last_wal_replay_lsn() "
                    + "ELSE pg_current_wal_flush_lsn() END)::text FROM pg_control_system() s "
                    + "CROSS JOIN pg_control_checkpoint() c",
                    connection
                )

            transaction |> Option.iter (fun value -> command.Transaction <- value)
            use! reader = command.ExecuteReaderAsync(CancellationToken.None)

            let! found = reader.ReadAsync(CancellationToken.None)

            if not found then
                invalidOp "Restored PostgreSQL control data is unavailable."

            let systemId = reader.GetString(0)
            let timeline = reader.GetInt64(1)
            let wal = reader.GetString(2)

            let! duplicated = reader.ReadAsync(CancellationToken.None)

            if duplicated || timeline < 1L then
                invalidOp "Restored PostgreSQL control data is ambiguous."

            return systemId, timeline, wal

        }

    let private installation (connection: NpgsqlConnection) transaction =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT installation_id,lineage_id,witness_epoch,writer_generation "
                    + "FROM claimcore.installation_lineage WHERE singleton",
                    connection,
                    transaction
                )

            use! reader = command.ExecuteReaderAsync(CancellationToken.None)

            let! found = reader.ReadAsync(CancellationToken.None)

            if not found then
                invalidOp "Restored installation identity is unavailable."

            let identity =
                reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3)

            let! duplicated = reader.ReadAsync(CancellationToken.None)

            if duplicated then
                invalidOp "Restored installation identity is ambiguous."

            return identity

        }

    let private authorityRevision (connection: NpgsqlConnection) transaction =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT revision FROM claimcore.authority_tip WHERE singleton",
                    connection,
                    transaction
                )

            let! result = command.ExecuteScalarAsync(CancellationToken.None)

            match result with
            | :? int64 as value when value > 0L -> return value
            | _ -> return invalidOp "Restored authority revision is unavailable."

        }

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

    let private witnessSystemInfo (witnessOwnerConnection: string) =
        task {
            let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection)

            if builder.Username <> "claimcore_witness_owner" then
                invalidOp "Independent witness owner identity is invalid."

            use witnessOwner = new NpgsqlConnection(builder.ConnectionString)
            do! witnessOwner.OpenAsync(CancellationToken.None)
            return! systemInfo witnessOwner None

        }

    let private inspectWith
        allowPendingHandoff
        (primary: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witnessOwnerConnection: string)
        (witness: WitnessProtocol)
        (audit: DataAuditSummary)
        (tip: Snapshot)
        =
        task {
            let! installationId, lineageId, epoch, writerGeneration =
                installation primary transaction

            let! authority = authorityRevision primary transaction

            let! primarySystem, primaryTimeline, primaryWal =
                systemInfo primary (Some transaction)

            let! copyCount, inventory =
                ManagedCopyInventoryDigest.compute primary transaction CancellationToken.None

            let! witnessSystem, witnessTimeline, witnessWal =
                witnessSystemInfo witnessOwnerConnection

            requirePair
                witness
                audit
                tip
                (installationId, lineageId, epoch, writerGeneration)
                primarySystem
                witnessSystem
                allowPendingHandoff

            return
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
        }

    let inspect primary transaction witnessOwnerConnection witness audit tip =
        inspectWith false primary transaction witnessOwnerConnection witness audit tip

    /// A SETTLE verifier audits the exact pending W1 ticket without opening case work.
    let inspectForHandoff primary transaction witnessOwnerConnection witness audit tip =
        if not tip.HandoffPending then
            invalidOp "A pending W1 handoff is unavailable."

        inspectWith true primary transaction witnessOwnerConnection witness audit tip

    let private walPosition (value: string) =
        let parts = value.Split('/')

        if parts.Length <> 2 then
            invalidOp "Restore WAL endpoint is invalid."

        let high =
            UInt64.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)

        let low =
            UInt64.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)

        if high > 0xFFFFFFFFUL || low > 0xFFFFFFFFUL then
            invalidOp "Restore WAL endpoint is invalid."

        (high <<< 32) ||| low

    let matchesLivePair
        (publication: TrustedRestorePublication)
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (facts: RestoredPairFacts)
        binarySha256
        =
        publication.ManifestSha256 = index.PublicationManifestSha256
        && publication.VerifierBinarySha256 = binarySha256
        && report.VerifierBinarySha256 = binarySha256
        && publication.ReportSignerKeyId = report.SignerKeyId
        && publication.CheckpointSignerKeyId = index.CheckpointSignerKeyId
        && publication.ReportSignerKeyId <> publication.CheckpointSignerKeyId
        && publication.InstallationId = facts.InstallationId
        && publication.LineageId = facts.LineageId
        && publication.Epoch = facts.Epoch
        && publication.WriterGeneration = facts.WriterGeneration
        && publication.WitnessCutoff = facts.WitnessCutoff
        && publication.WitnessCutoffHash = facts.WitnessCutoffHash
        && report.InstallationId = facts.InstallationId
        && report.LineageId = facts.LineageId
        && report.Epoch = facts.Epoch
        && report.AuthorityRevision = facts.AuthorityRevision
        && report.PrimarySystemId = facts.PrimarySystemId
        && report.PrimaryTimeline = facts.PrimaryTimeline
        && report.WitnessSystemId = facts.WitnessSystemId
        && report.WitnessTimeline = facts.WitnessTimeline
        && report.WitnessCutoff = facts.WitnessCutoff
        && report.WitnessCutoffHash = facts.WitnessCutoffHash
        && report.CatalogManifestSha256 = facts.CatalogManifestSha256
        && facts.PendingIntents = 0L
        && walPosition facts.PrimaryWalEndpoint >= walPosition index.PrimaryWalEndpoint
        && walPosition facts.WitnessWalEndpoint >= walPosition index.WitnessWalEndpoint
