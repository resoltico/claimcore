namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Witness
open DataAuditCommon

/// Verifies a consistent primary snapshot against one independent witness cutoff. A caller must
/// fence writers before using this as restore admission; same-host tests do not prove independent
/// survival or custody of the witness.
module internal DataAudit =
    [<NoEquality; NoComparison>]
    type private InstallationEvidence =
        {
            Zone: TimeZoneInfo
            InstallationId: Guid
            LineageId: Guid
            Epoch: int64
            Use: InstallationUseState
            LossRetired: bool
            LossRetirementId: Guid option
            LossRetirementIntentSequence: int64 option
            LossRetirementIntentHash: byte array option
        }

    let private readEvidence (reader: NpgsqlDataReader) =
        {
            Zone = businessZone (reader.GetString(0))
            InstallationId = reader.GetGuid(1)
            LineageId = reader.GetGuid(2)
            Epoch = reader.GetInt64(3)
            Use =
                {
                    Scope = InstallationUse.parseScope (reader.GetString(4))
                    Phase = InstallationUse.parsePhase (reader.GetString(5))
                    ActivationEventId = if reader.IsDBNull(6) then None else Some(reader.GetGuid(6))
                    ActivationSequence =
                        if reader.IsDBNull(7) then
                            None
                        else
                            Some(reader.GetInt64(7))
                    ActivationHash =
                        if reader.IsDBNull(8) then
                            None
                        else
                            Some(reader.GetFieldValue<byte array>(8))
                }
            LossRetired = reader.GetBoolean(9)
            LossRetirementId =
                if reader.IsDBNull(10) then
                    None
                else
                    Some(reader.GetGuid(10))
            LossRetirementIntentSequence =
                if reader.IsDBNull(11) then
                    None
                else
                    Some(reader.GetInt64(11))
            LossRetirementIntentHash =
                if reader.IsDBNull(12) then
                    None
                else
                    Some(reader.GetFieldValue<byte array>(12))
        }

    let private lossMatchesWitness (tip: Snapshot) (evidence: InstallationEvidence) =
        if tip.LossRetirementPending || tip.LossRetired then
            if evidence.LossRetired then
                evidence.LossRetirementId = tip.LossRetirementId
                && evidence.LossRetirementIntentSequence = tip.LossRetirementIntentSequence
                && evidence.LossRetirementIntentHash = tip.LossRetirementIntentHash
            else
                tip.LossRetirementPending
                && evidence.LossRetirementId.IsNone
                && evidence.LossRetirementIntentSequence.IsNone
                && evidence.LossRetirementIntentHash.IsNone
        else
            not evidence.LossRetired
            && evidence.LossRetirementId.IsNone
            && evidence.LossRetirementIntentSequence.IsNone
            && evidence.LossRetirementIntentHash.IsNone

    let private matchesWitness (tip: Snapshot) (evidence: InstallationEvidence) =
        tip.Identity.InstallationId = evidence.InstallationId
        && tip.Identity.LineageId = evidence.LineageId
        && tip.Identity.Epoch = evidence.Epoch
        && tip.Use.Scope = evidence.Use.Scope
        && tip.Use.Phase = evidence.Use.Phase
        && tip.Use.ActivationEventId = evidence.Use.ActivationEventId
        && tip.Use.ActivationSequence = evidence.Use.ActivationSequence
        && tip.Use.ActivationHash = evidence.Use.ActivationHash
        && lossMatchesWitness tip evidence

    let private installation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT business_time_zone,installation_id,lineage_id,witness_epoch,"
                    + "data_use_scope,data_use_phase,data_use_activation_event_id,"
                    + "data_use_activation_sequence,data_use_activation_hash,"
                    + "loss_retired,loss_retirement_id,loss_retirement_intent_sequence,"
                    + "loss_retirement_intent_hash "
                    + "FROM claimcore.installation_lineage WHERE singleton",
                    connection,
                    transaction
                )

            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let! found = reader.ReadAsync(cancellationToken)

            if not found then
                corrupt ()

            let evidence = readEvidence reader
            reader.Close()
            let tip = witnessProof witness.Snapshot

            if not (matchesWitness tip evidence) then
                corrupt ()

            return evidence.Zone, tip
        }

    let private requireNoOrphan
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.case_changes h LEFT JOIN claimcore.cases c ON c.case_reference=h.case_reference WHERE c.case_reference IS NULL)",
                    connection,
                    transaction
                )

            let! value = command.ExecuteScalarAsync(cancellationToken)

            if unbox<bool> value then
                corrupt ()
        }

    let private verifyTechnical connection transaction witness cutoff cancellationToken =
        task {
            do!
                DataAuditTechnical.verifyPreparations
                    connection
                    transaction
                    witness
                    cutoff
                    cancellationToken

            do!
                DataAuditTechnical.verifyAttempts
                    connection
                    transaction
                    witness
                    cutoff
                    cancellationToken
        }

    let private verifyPrimary
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        zone
        (witness: WitnessProtocol)
        cutoff
        (commitments: ISuppressionCommitments option)
        (cancellationToken: CancellationToken)
        =
        task {
            let! cases, operations, lifecycleEvents, verifiedCaseTipsSha256 =
                DataAuditCaseReplay.replayCases
                    connection
                    transaction
                    zone
                    witness
                    cutoff
                    cancellationToken

            do! requireNoOrphan connection transaction cancellationToken

            let! erasureFences, terminalApprovals, terminalEvents =
                DataAuditErasureFences.verify
                    connection
                    transaction
                    witness
                    cutoff
                    commitments
                    cancellationToken

            let! revocations =
                DataAuditWitness.verifyRevocations
                    connection
                    transaction
                    witness
                    cutoff
                    cancellationToken

            do! verifyTechnical connection transaction witness cutoff cancellationToken

            return
                {
                    Cases = cases
                    AcceptedOperations = operations
                    LifecycleEvents = lifecycleEvents
                    VerifiedCaseTipsSha256 = verifiedCaseTipsSha256
                    ErasureFences = erasureFences
                    TerminalApprovals = terminalApprovals
                    TerminalEvents = terminalEvents
                    Revocations = revocations
                }
        }


    let runWithSuppression
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments option)
        (cancellationToken: CancellationToken)
        =
        task {
            witnessProof witness.AdmitReadOnly

            use! transaction =
                connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)

            use readOnly =
                new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)

            let! _ = readOnly.ExecuteNonQueryAsync(cancellationToken)
            let! zone, tip = installation connection transaction witness cancellationToken

            let! primary =
                verifyPrimary
                    connection
                    transaction
                    zone
                    witness
                    tip.TipSequence
                    commitments
                    cancellationToken

            let! authority =
                DataAuditAuthority.verify connection transaction witness tip cancellationToken

            let finalTip = witnessProof witness.Snapshot

            if
                finalTip.TipSequence <> tip.TipSequence
                || finalTip.TipHash <> tip.TipHash
                || finalTip.ActiveKeyId <> tip.ActiveKeyId
            then
                corrupt ()

            do! transaction.CommitAsync(cancellationToken)

            return DataAuditSummary.create primary authority tip.TipSequence
        }

    let run connection witness cancellationToken =
        runWithSuppression connection witness None cancellationToken
