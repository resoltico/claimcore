namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

/// Primary projection is released only after the witness's exact settled ticket is read back.
module internal InstallationUseActivationPrimary =
    let private insertSql =
        "INSERT INTO claimcore.installation_data_use_activations "
        + "(activation_id,installation_id,lineage_id,witness_epoch,writer_generation,"
        + "activation_plan_sha256,health_certificate_sha256,approval_one_id,approval_two_id,"
        + "canonical_action,candidate_sha256,"
        + "witness_intent_sequence,witness_intent_hash,witness_sequence,witness_entry_hash) "
        + "VALUES (@event,@installation,@lineage,@epoch,@generation,@plan,@health,"
        + "@approvalOne,@approvalTwo,@canonical,@candidate,@intentSequence,@intentHash,"
        + "@settledSequence,@settledHash)"

    let state (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) =
        use command =
            new NpgsqlCommand(
                "SELECT installation_id,lineage_id,witness_epoch,writer_generation,"
                + "data_use_scope,data_use_phase,data_use_activation_event_id,"
                + "data_use_activation_sequence,data_use_activation_hash "
                + "FROM claimcore.installation_lineage WHERE singleton FOR UPDATE",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Primary data-use installation is unavailable."

        let identity = reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2)
        let generation = reader.GetInt64(3)
        let scope = InstallationUse.parseScope (reader.GetString(4))
        let phase = InstallationUse.parsePhase (reader.GetString(5))
        let eventId = if reader.IsDBNull(6) then None else Some(reader.GetGuid(6))

        let sequence =
            if reader.IsDBNull(7) then
                None
            else
                Some(reader.GetInt64(7))

        let hash =
            if reader.IsDBNull(8) then
                None
            else
                Some(reader.GetFieldValue<byte array>(8))

        if reader.Read() then
            invalidOp "Primary data-use installation is ambiguous."

        identity, generation, scope, phase, eventId, sequence, hash

    let private insertExact
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (record: InstallationUseActivationRecord)
        (canonical: byte array)
        (intent: int64 * byte array)
        (settled: int64 * byte array)
        =
        use command = new NpgsqlCommand(insertSql, connection, transaction)

        Sql.uuid command "event" record.EventId
        Sql.uuid command "installation" record.InstallationId
        Sql.uuid command "lineage" record.LineageId
        Sql.integer command "epoch" record.Epoch
        Sql.integer command "generation" record.WriterGeneration

        Sql.add command "plan" NpgsqlDbType.Bytea (box record.PlanSha256)

        Sql.add command "health" NpgsqlDbType.Bytea (box record.HealthCertificateSha256)

        Sql.uuid command "approvalOne" record.Approvals.First.ApprovalId
        Sql.uuid command "approvalTwo" record.Approvals.Second.ApprovalId

        Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
        Sql.add command "candidate" NpgsqlDbType.Bytea (box (SHA256.HashData canonical))
        Sql.integer command "intentSequence" (fst intent)
        Sql.add command "intentHash" NpgsqlDbType.Bytea (box (snd intent))
        Sql.integer command "settledSequence" (fst settled)
        Sql.add command "settledHash" NpgsqlDbType.Bytea (box (snd settled))

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Primary data-use event was not retained."

    let insert
        connection
        transaction
        (proof: BackupHealthQualifiedEvidence)
        (plan: BackupHealthActivationPlan)
        (approvals: InstallationUseApprovalPair)
        eventId
        canonical
        intent
        settled
        =
        let record =
            InstallationUseActivationCodec.decode canonical
            |> Option.defaultWith (fun () -> invalidOp "Activation canonical evidence is invalid.")

        if
            record.EventId <> eventId
            || record.PlanSha256 <> Convert.FromHexString plan.PlanSha256
            || record.HealthCertificateSha256 <> Convert.FromHexString proof.CertificateSha256
            || record.Approvals.First.ApprovalId <> approvals.First.ApprovalId
            || record.Approvals.Second.ApprovalId <> approvals.Second.ApprovalId
        then
            invalidOp "Activation candidate differs from qualified source."

        insertExact connection transaction record canonical intent settled

    let insertHistorical
        connection
        transaction
        (record: InstallationUseActivationRecord)
        canonical
        intent
        settled
        =
        insertExact connection transaction record canonical intent settled

    let release
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        eventId
        (settled: int64 * byte array)
        =
        use command =
            new NpgsqlCommand(
                "UPDATE claimcore.installation_lineage SET data_use_phase='ACTIVE',"
                + "data_use_activation_event_id=@event,data_use_activation_sequence=@sequence,"
                + "data_use_activation_hash=@hash WHERE singleton AND data_use_scope='REAL_DATA' "
                + "AND data_use_phase='BOOTSTRAP_NO_CASES'",
                connection,
                transaction
            )

        Sql.uuid command "event" eventId
        Sql.integer command "sequence" (fst settled)
        Sql.add command "hash" NpgsqlDbType.Bytea (box (snd settled))

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Primary data-use phase did not advance."

    let requireExisting
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (record: InstallationUseActivationRecord)
        (canonical: byte array)
        (intent: int64 * byte array)
        (settled: int64 * byte array)
        =
        use command =
            new NpgsqlCommand(
                "SELECT canonical_action,candidate_sha256,activation_plan_sha256,"
                + "health_certificate_sha256,approval_one_id,approval_two_id,"
                + "witness_intent_sequence,witness_intent_hash,witness_sequence,witness_entry_hash "
                + "FROM claimcore.installation_data_use_activations WHERE activation_id=@event",
                connection,
                transaction
            )

        Sql.uuid command "event" record.EventId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Primary data-use event is absent."

        let same =
            reader.GetFieldValue<byte array>(0) = canonical
            && reader.GetFieldValue<byte array>(1) = SHA256.HashData(canonical)
            && reader.GetFieldValue<byte array>(2) = record.PlanSha256
            && reader.GetFieldValue<byte array>(3) = record.HealthCertificateSha256
            && reader.GetGuid(4) = record.Approvals.First.ApprovalId
            && reader.GetGuid(5) = record.Approvals.Second.ApprovalId
            && reader.GetInt64(6) = fst intent
            && reader.GetFieldValue<byte array>(7) = snd intent
            && reader.GetInt64(8) = fst settled
            && reader.GetFieldValue<byte array>(9) = snd settled
            && not (reader.Read())

        if not same then
            invalidOp "Primary data-use exact retry diverged."
