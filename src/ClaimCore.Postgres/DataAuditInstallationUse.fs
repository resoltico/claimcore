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
    let private plans connection transaction witness cutoff (ct: CancellationToken) =
        task {
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
                use! reader = command.ExecuteReaderAsync(ct)

                while! reader.ReadAsync(ct) do
                    ids.Add(reader.GetGuid(0))

                reader.Close()

                for planId in ids do
                    let! observed =
                        witnessProofAsync (fun () ->
                            InstallationUsePlanRead.verified
                                connection
                                transaction
                                witness
                                planId
                                ct)

                    let published = observed |> Option.defaultWith corrupt

                    if published.SettlementSequence > cutoff then
                        corrupt ()

                    cursor <- planId

                more <- ids.Count = 256
        }

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

    let private activationRows connection transaction (ct: CancellationToken) =
        task {
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

            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<InstallationUseActivationRow>()

            while! reader.ReadAsync(ct) do
                rows.Add(readRow reader)

            return rows |> Seq.toList
        }

    let private sameIdentity
        (row: InstallationUseActivationRow)
        (record: InstallationUseActivationRecord)
        (state: InstallationUseState)
        (snapshot: Snapshot)
        =
        state.ActivationEventId = Some row.EventId
        && state.ActivationSequence = Some row.SettlementSequence
        && state.ActivationHash = Some row.SettlementHash
        && row.InstallationId = snapshot.Identity.InstallationId
        && row.LineageId = snapshot.Identity.LineageId
        && row.Epoch = snapshot.Identity.Epoch
        && row.Generation = snapshot.WriterGeneration
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

    let private approvalUses
        connection
        transaction
        (row: InstallationUseActivationRow)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT approval_id,slot FROM claimcore.installation_data_use_approval_uses "
                    + "WHERE activation_id=@activation ORDER BY slot",
                    connection,
                    transaction
                )

            Sql.uuid command "activation" row.EventId
            use! reader = command.ExecuteReaderAsync(ct)

            let! first = reader.ReadAsync(ct)

            if not first || reader.GetGuid(0) <> row.ApprovalOneId || reader.GetInt32(1) <> 1 then
                corrupt ()

            let! second = reader.ReadAsync(ct)

            if not second || reader.GetGuid(0) <> row.ApprovalTwoId || reader.GetInt32(1) <> 2 then
                corrupt ()

            let! extra = reader.ReadAsync(ct)

            if extra then
                corrupt ()
        }

    let private verifiedPlan
        connection
        transaction
        witness
        (record: InstallationUseActivationRecord)
        (row: InstallationUseActivationRow)
        ct
        =
        task {
            let! observed =
                witnessProofAsync (fun () ->
                    InstallationUsePlanRead.verified
                        connection
                        transaction
                        witness
                        record.PlanId
                        ct)

            let published = observed |> Option.defaultWith corrupt

            if
                published.ActivationId <> row.EventId
                || Convert.FromHexString(published.Plan.PlanSha256) <> row.PlanSha256
            then
                corrupt ()

            return published
        }

    let private verifyActive
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (state: InstallationUseState)
        (row: InstallationUseActivationRow)
        (ct: CancellationToken)
        =
        task {
            let record =
                InstallationUseActivationCodec.decode row.Canonical
                |> Option.defaultWith corrupt

            let! snapshot = witness.Snapshot(ct)

            if
                not (sameIdentity row record state snapshot)
                || not (sameEvidence row record cutoff)
            then
                corrupt ()

            do!
                witnessProofAsync (fun () ->
                    witness.VerifyHistoricalTip(
                        record.ExpectedWitnessSequence,
                        record.ExpectedWitnessHash,
                        ct
                    ))

            let! published = verifiedPlan connection transaction witness record row ct

            let! _ =
                witnessProofAsync (fun () ->
                    InstallationUseActivationApprovals.verifyHistorical
                        connection
                        transaction
                        witness
                        published
                        record
                        ct)

            do! approvalUses connection transaction row ct

            let! _ =
                witnessProofAsync (fun () ->
                    WriterActivationWitness.verifyHistorical
                        witness
                        row.EventId
                        row.Canonical
                        (row.IntentSequence, row.IntentHash)
                        (row.SettlementSequence, row.SettlementHash)
                        ct)

            return ()
        }

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        (ct: CancellationToken)
        =
        task {
            do! plans connection transaction witness cutoff ct

            let! _ =
                DataAuditInstallationUseApprovals.verifyAll connection transaction witness cutoff ct

            let! snapshot = witness.Snapshot(ct)
            let state = snapshot.Use
            let! rows = activationRows connection transaction ct

            match state.Scope, state.Phase, rows with
            | InstallationUseScope.SyntheticOnly, InstallationUsePhase.Active, []
            | InstallationUseScope.RealData, InstallationUsePhase.BootstrapNoCases, [] -> return ()
            | InstallationUseScope.RealData, InstallationUsePhase.Active, [ row ] ->
                return! verifyActive connection transaction witness cutoff state row ct
            | _ -> return corrupt ()
        }
