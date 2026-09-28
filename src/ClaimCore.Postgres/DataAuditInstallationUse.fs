namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness
open WitnessProtocolReconciliation
open DataAuditCommon

[<NoEquality; NoComparison>]
type private InstallationUseActivationRow =
    {
        EventId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        Generation: int64
        PlanSha256: byte array
        HealthSha256: byte array
        ApprovalOneId: Guid
        ApprovalTwoId: Guid
        Canonical: byte array
        Digest: byte array
        IntentSequence: int64
        IntentHash: byte array
        SettlementSequence: int64
        SettlementHash: byte array
    }

/// Full-audit bridge from immutable installation scope to witnessed plan and release.
module internal DataAuditInstallationUse =
    let private plans connection transaction witness cutoff =
        let mutable cursor = Guid.Empty
        let mutable more = true

        while more do
            let ids = ResizeArray<Guid>()

            use command =
                new NpgsqlCommand(
                    "SELECT plan_id FROM claimcore.installation_data_use_plans "
                    + "WHERE plan_id > @cursor ORDER BY plan_id LIMIT 256",
                    connection,
                    transaction
                )

            Sql.uuid command "cursor" cursor
            use reader = command.ExecuteReader()

            while reader.Read() do
                ids.Add(reader.GetGuid(0))

            reader.Close()

            for planId in ids do
                let published =
                    witnessProof (fun () ->
                        InstallationUsePlanRead.verified
                            connection
                            transaction
                            witness
                            planId
                            CancellationToken.None
                        |> fun task -> task.GetAwaiter().GetResult())
                    |> Option.defaultWith (fun () -> corrupt ())

                if published.SettlementSequence > cutoff then
                    corrupt ()

                cursor <- planId

            more <- ids.Count = 256

    let private readRow (reader: System.Data.Common.DbDataReader) =
        {
            EventId = reader.GetGuid(0)
            InstallationId = reader.GetGuid(1)
            LineageId = reader.GetGuid(2)
            Epoch = reader.GetInt64(3)
            Generation = reader.GetInt64(4)
            PlanSha256 = reader.GetFieldValue<byte array>(5)
            HealthSha256 = reader.GetFieldValue<byte array>(6)
            ApprovalOneId = reader.GetGuid(7)
            ApprovalTwoId = reader.GetGuid(8)
            Canonical = reader.GetFieldValue<byte array>(9)
            Digest = reader.GetFieldValue<byte array>(10)
            IntentSequence = reader.GetInt64(11)
            IntentHash = reader.GetFieldValue<byte array>(12)
            SettlementSequence = reader.GetInt64(13)
            SettlementHash = reader.GetFieldValue<byte array>(14)
        }

    let private activationRows connection transaction =
        use command =
            new NpgsqlCommand(
                "SELECT activation_id,installation_id,lineage_id,witness_epoch,writer_generation,"
                + "activation_plan_sha256,health_certificate_sha256,approval_one_id,approval_two_id,"
                + "canonical_action,candidate_sha256,witness_intent_sequence,witness_intent_hash,"
                + "witness_sequence,witness_entry_hash "
                + "FROM claimcore.installation_data_use_activations ORDER BY activation_id LIMIT 2",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()
        let rows = ResizeArray<InstallationUseActivationRow>()

        while reader.Read() do
            rows.Add(readRow reader)

        rows |> Seq.toList

    let private sameIdentity
        (row: InstallationUseActivationRow)
        (record: InstallationUseActivationRecord)
        (state: InstallationUseState)
        (witness: WitnessProtocol)
        =
        state.ActivationEventId = Some row.EventId
        && state.ActivationSequence = Some row.SettlementSequence
        && state.ActivationHash = Some row.SettlementHash
        && row.InstallationId = witness.Identity.InstallationId
        && row.LineageId = witness.Identity.LineageId
        && row.Epoch = witness.Identity.Epoch
        && row.Generation = witness.Snapshot().WriterGeneration
        && record.EventId = row.EventId
        && record.InstallationId = row.InstallationId
        && record.LineageId = row.LineageId
        && record.Epoch = row.Epoch
        && record.WriterGeneration = row.Generation

    let private sameEvidence
        (row: InstallationUseActivationRow)
        (record: InstallationUseActivationRecord)
        cutoff
        =
        record.PlanSha256 = row.PlanSha256
        && record.HealthCertificateSha256 = row.HealthSha256
        && record.Approvals.First.ApprovalId = row.ApprovalOneId
        && record.Approvals.Second.ApprovalId = row.ApprovalTwoId
        && record.ExpectedWitnessSequence + 1L = row.IntentSequence
        && row.HealthSha256.Length = 32
        && row.Digest = SHA256.HashData(row.Canonical)
        && row.IntentSequence + 1L = row.SettlementSequence
        && row.SettlementSequence <= cutoff

    let private approvalUses connection transaction (row: InstallationUseActivationRow) =
        use command =
            new NpgsqlCommand(
                "SELECT approval_id,slot FROM claimcore.installation_data_use_approval_uses "
                + "WHERE activation_id=@activation ORDER BY slot",
                connection,
                transaction
            )

        Sql.uuid command "activation" row.EventId
        use reader = command.ExecuteReader()

        if
            not (reader.Read())
            || reader.GetGuid(0) <> row.ApprovalOneId
            || reader.GetInt32(1) <> 1
            || not (reader.Read())
            || reader.GetGuid(0) <> row.ApprovalTwoId
            || reader.GetInt32(1) <> 2
            || reader.Read()
        then
            corrupt ()

    let private verifyActive
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (state: InstallationUseState)
        (row: InstallationUseActivationRow)
        =
        let record =
            InstallationUseActivationCodec.decode row.Canonical
            |> Option.defaultWith (fun () -> corrupt ())

        if
            not (sameIdentity row record state witness)
            || not (sameEvidence row record cutoff)
        then
            corrupt ()

        witnessProof (fun () ->
            witness.VerifyHistoricalTip(record.ExpectedWitnessSequence, record.ExpectedWitnessHash))

        let published =
            witnessProof (fun () ->
                InstallationUsePlanRead.verified
                    connection
                    transaction
                    witness
                    record.PlanId
                    CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult())
            |> Option.defaultWith (fun () -> corrupt ())

        if
            published.ActivationId <> row.EventId
            || Convert.FromHexString(published.Plan.PlanSha256) <> row.PlanSha256
        then
            corrupt ()

        witnessProof (fun () ->
            InstallationUseActivationApprovals.verifyHistorical
                connection
                transaction
                witness
                published
                record)
        |> ignore

        approvalUses connection transaction row

        witnessProof (fun () ->
            WriterActivationWitness.verifyHistorical
                witness
                row.EventId
                row.Canonical
                (row.IntentSequence, row.IntentHash)
                (row.SettlementSequence, row.SettlementHash))
        |> ignore

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        =
        plans connection transaction witness cutoff

        DataAuditInstallationUseApprovals.verifyAll
            connection
            transaction
            witness
            cutoff
            CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult() |> ignore

        let state = witness.Snapshot().Use

        match state.Scope, state.Phase, activationRows connection transaction with
        | InstallationUseScope.SyntheticOnly, InstallationUsePhase.Active, []
        | InstallationUseScope.RealData, InstallationUsePhase.BootstrapNoCases, [] -> ()
        | InstallationUseScope.RealData, InstallationUsePhase.Active, [ row ] ->
            verifyActive connection transaction witness cutoff state row
        | _ -> corrupt ()
