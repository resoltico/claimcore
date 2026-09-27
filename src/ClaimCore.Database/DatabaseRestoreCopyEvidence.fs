namespace ClaimCore.Database

open System
open Npgsql
open NpgsqlTypes

module internal DatabaseRestoreCopyEvidence =
    let private copyMatches
        (reader: Data.Common.DbDataReader)
        (report: RestoreReportClaims)
        cluster
        systemId
        timeline
        =
        reader.GetString(0) = "OWNER_ATTESTED"
        && reader.GetString(1) = cluster
        && reader.GetString(2) = "BASE"
        && reader.GetString(3) = "RETAINED"
        && reader.GetString(4) = systemId
        && int64 (reader.GetInt32(5)) = timeline
        && reader.GetInt64(6) = report.BackupCaptureSequence
        && Convert.ToHexStringLower(reader.GetFieldValue<byte array>(7)) = report.BackupCaptureHash

    let private requireOne
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (report: RestoreReportClaims)
        (copyId: Guid)
        (cluster: string)
        (systemId: string)
        (timeline: int64)
        =
        use command =
            new NpgsqlCommand(
                "SELECT producer_kind,cluster_name,copy_kind,state,postgres_system_id,"
                + "timeline,witness_cutoff_sequence,witness_cutoff_hash,last_verified_at,"
                + "retain_until,verification_proof_sha256 "
                + "FROM claimcore.managed_copies WHERE copy_id=@copy",
                connection,
                transaction
            )

        command.Parameters.AddWithValue("copy", NpgsqlDbType.Uuid, copyId) |> ignore
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "A signed backup copy is absent from witnessed primary inventory."

        let retainedUntil = reader.GetFieldValue<DateTimeOffset>(9)

        if
            not (copyMatches reader report cluster systemId timeline)
            || reader.IsDBNull(8)
            || reader.IsDBNull(10)
            || reader.GetFieldValue<byte array>(10).Length <> 32
            || retainedUntil <= report.ValidUntil
            || reader.Read()
        then
            invalidOp "A signed backup copy is not verified and retained at the exact cutoff."

    let verify connection transaction (report: RestoreReportClaims) primaryCopy witnessCopy =
        if primaryCopy = witnessCopy then
            invalidOp "Restored primary and witness cannot share a backup copy ID."

        requireOne
            connection
            transaction
            report
            primaryCopy
            "PRIMARY"
            report.PrimarySystemId
            report.PrimaryTimeline

        requireOne
            connection
            transaction
            report
            witnessCopy
            "WITNESS"
            report.WitnessSystemId
            report.WitnessTimeline
