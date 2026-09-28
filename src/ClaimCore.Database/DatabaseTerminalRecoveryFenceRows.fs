namespace ClaimCore.Database

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres

[<NoEquality; NoComparison>]
type internal TerminalRecoveryFenceRow =
    {
        ActivationId: Guid
        SettlementSequence: int64
        SettlementHash: byte array
        ArtifactCutoffSequence: int64
        AuthorityRevision: int64
        AuthorityHash: byte array
    }

/// Reads only immutable W2 metadata and current erasure/artifact fences under the owner lock.
module internal DatabaseTerminalRecoveryFenceRows =
    let private activationSql =
        "SELECT a.canonical_action,a.candidate_sha256,a.witness_intent_sequence,"
        + "a.witness_intent_hash,a.witness_sequence,a.witness_epoch,a.witness_entry_hash,"
        + "l.writer_generation,l.writer_activation_pending,l.writer_activation_event_id,"
        + "l.writer_activation_sequence,l.writer_activation_hash,h.old_generation,h.new_generation "
        + "FROM claimcore.writer_activations a "
        + "JOIN claimcore.writer_handoffs h ON h.handoff_id=a.handoff_id "
        + "CROSS JOIN claimcore.installation_lineage l "
        + "WHERE a.activation_id=@activation AND l.singleton FOR UPDATE OF l,h"

    let private currentW2
        (witness: WitnessProtocol)
        activationId
        settlementSequence
        settlementHash
        (proposal: TerminalFinalProposal)
        =
        let snapshot = witness.Snapshot()

        if
            snapshot.ActivationPending
            || snapshot.ActivationEventId <> Some activationId
            || snapshot.ActivationSequence <> Some settlementSequence
            || snapshot.ActivationHash <> Some settlementHash
            || snapshot.WriterGeneration <> proposal.NewWriterGeneration
        then
            invalidOp "Terminal writer activation is not current."

    let private activation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (candidate: WriterActivationEvidence)
        (proposal: TerminalFinalProposal)
        =
        let activationId = WriterActivationCandidate.activationId candidate.HandoffId
        let canonical = WriterActivationCandidate.encode candidate

        try
            use command = new NpgsqlCommand(activationSql, connection, transaction)
            Sql.uuid command "activation" activationId
            use reader = command.ExecuteReader()

            if not (reader.Read()) then
                invalidOp "Terminal W2 activation is absent."

            let intentSequence = reader.GetInt64(2)
            let intentHash = reader.GetFieldValue<byte array>(3)
            let settlementSequence = reader.GetInt64(4)
            let epoch = reader.GetInt64(5)
            let settlementHash = reader.GetFieldValue<byte array>(6)

            let valid =
                reader.GetFieldValue<byte array>(0) = canonical
                && reader.GetFieldValue<byte array>(1) = SHA256.HashData(canonical)
                && epoch = witness.Identity.Epoch
                && reader.GetInt64(7) = candidate.WriterGeneration
                && not (reader.GetBoolean(8))
                && reader.GetGuid(9) = activationId
                && reader.GetInt64(10) = settlementSequence
                && reader.GetFieldValue<byte array>(11) = settlementHash
                && reader.GetInt64(12) = proposal.OldWriterGeneration
                && reader.GetInt64(13) = proposal.NewWriterGeneration
                && settlementSequence > intentSequence
                && not (reader.Read())

            if not valid then
                invalidOp "Terminal W2 authority diverged."

            reader.Close()

            WriterActivationWitness.verifyHistorical
                witness
                activationId
                canonical
                (intentSequence, intentHash)
                (settlementSequence, settlementHash)
            |> ignore

            currentW2 witness activationId settlementSequence settlementHash proposal

            activationId, settlementSequence, settlementHash
        finally
            CryptographicOperations.ZeroMemory(canonical)

    let private tombstone
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (proposal: TerminalFinalProposal)
        =
        use command =
            new NpgsqlCommand(
                "SELECT authority_revision,authority_hash,phase,retention_policy_id,"
                + "suppression_until FROM claimcore.case_erasure_tombstones "
                + "WHERE case_id=@case FOR UPDATE",
                connection,
                transaction
            )

        Sql.uuid command "case" proposal.Copy.CaseId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Terminal case tombstone is absent."

        let revision = reader.GetInt64(0)
        let hash = reader.GetFieldValue<byte array>(1)

        let valid =
            revision = proposal.Copy.ExpectedAuthorityRevision
            && hash = Convert.FromHexString(proposal.Copy.ExpectedAuthorityHash)
            && reader.GetString(2) = "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
            && reader.GetString(3) = proposal.Copy.PolicyId
            && reader.GetFieldValue<DateTimeOffset>(4) = proposal.Copy.SuppressionUntil
            && not (reader.Read())

        if not valid then
            invalidOp "Terminal case authority diverged."

        revision, hash

    let private artifactCutoff
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        caseId
        (observedAt: DateTimeOffset)
        =
        use command =
            new NpgsqlCommand(
                "SELECT COALESCE(MAX(e.witness_sequence),1),"
                + "COUNT(*) FILTER (WHERE e.expires_at>@instant OR p.export_id IS NOT NULL) "
                + "FROM claimcore.recovery_artifact_exports e "
                + "LEFT JOIN claimcore.recovery_artifact_payloads p ON p.export_id=e.export_id "
                + "WHERE e.case_id=@case",
                connection,
                transaction
            )

        Sql.uuid command "case" caseId
        Sql.add command "instant" NpgsqlTypes.NpgsqlDbType.TimestampTz (box observedAt)
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Terminal artifact inventory is unavailable."

        let cutoff = reader.GetInt64(0)
        let active = reader.GetInt64(1)

        if reader.Read() || active <> 0L then
            invalidOp "Terminal recovery artifacts remain admissible."

        cutoff

    let verify connection transaction witness candidate proposal observedAt =
        let activationId, settlementSequence, settlementHash =
            activation connection transaction witness candidate proposal

        let revision, hash = tombstone connection transaction proposal
        let cutoff = artifactCutoff connection transaction proposal.Copy.CaseId observedAt

        if cutoff >= settlementSequence then
            invalidOp "Terminal recovery artifact was issued after writer fencing."

        {
            ActivationId = activationId
            SettlementSequence = settlementSequence
            SettlementHash = settlementHash
            ArtifactCutoffSequence = cutoff
            AuthorityRevision = revision
            AuthorityHash = hash
        }
