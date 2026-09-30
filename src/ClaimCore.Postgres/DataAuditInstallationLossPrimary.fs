namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

/// Replays terminal owner signatures, exact primary receipt and retained HMAC denials.
module internal DataAuditInstallationLossPrimary =
    let private signer (connection: NpgsqlConnection) transaction keyId owner canonical signature =
        use command =
            new NpgsqlCommand(
                "SELECT s.ed25519_public_key,s.signer_purpose,s.holder_actor_id,"
                + "s.active,a.principal_kind,a.enabled,g.active "
                + "FROM claimcore.managed_copy_signers s "
                + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
                + "JOIN claimcore.actor_grants g ON g.actor_id=a.actor_id "
                + "AND g.scope_kind='INSTALLATION' "
                + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000'::uuid "
                + "AND g.role_name='OWNER' WHERE s.signing_key_id=@key",
                connection,
                transaction
            )

        Sql.uuid command "key" keyId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            corrupt ()

        let key = reader.GetFieldValue<byte array>(0)

        if
            reader.GetString(1) <> "INSTALLATION_LOSS_RETIREMENT"
            || reader.GetGuid(2) <> owner
            || not (reader.GetBoolean(3))
            || reader.GetString(4) <> "HUMAN"
            || not (reader.GetBoolean(5))
            || not (reader.GetBoolean(6))
            || not (ManagedCopySignature.verify key canonical signature)
            || reader.Read()
        then
            corrupt ()

    let signatures (connection: NpgsqlConnection) transaction (row: LossRetirementEvidence) =
        signer
            connection
            transaction
            row.SignerOneId
            row.OwnerOneActorId
            row.Canonical
            row.SignatureOne

        signer
            connection
            transaction
            row.SignerTwoId
            row.OwnerTwoActorId
            row.Canonical
            row.SignatureTwo

    let private receiptMatches
        (reader: System.Data.Common.DbDataReader)
        (row: LossRetirementEvidence)
        =
        reader.GetFieldValue<byte array>(0) = row.Canonical
        && reader.GetFieldValue<byte array>(1) = row.CanonicalSha256
        && reader.GetFieldValue<byte array>(2) = row.SignatureOne
        && reader.GetFieldValue<byte array>(3) = row.SignatureTwo
        && reader.GetInt64(4) = row.PreviousSequence
        && reader.GetFieldValue<byte array>(5) = row.PreviousHash
        && reader.GetInt64(6) = row.IntentSequence
        && reader.GetFieldValue<byte array>(7) = row.IntentHash
        && reader.GetInt32(8) = row.KnownOperationCount
        && reader.GetFieldValue<byte array>(9) = row.KnownOperationDigest
        && reader.GetBoolean(10)
        && reader.GetGuid(11) = row.RetirementId
        && reader.GetInt64(12) = row.IntentSequence
        && reader.GetFieldValue<byte array>(13) = row.IntentHash

    let private optionalBytes (reader: System.Data.Common.DbDataReader) index =
        if reader.IsDBNull(index) then
            None
        else
            Some(reader.GetFieldValue<byte array>(index))

    let private decisionMatches
        (reader: System.Data.Common.DbDataReader)
        (value: InstallationLossRetirementDecision)
        =
        reader.GetGuid(14) = value.InstallationId
        && reader.GetGuid(15) = value.LineageId
        && reader.GetInt64(16) = value.Epoch
        && optionalBytes reader 17 = value.EvidenceReportSha256
        && optionalBytes reader 18 = value.IndependentCheckpointSha256
        && reader.GetString(19) =
            InstallationLossRetirementCandidate.operationSetName value.OperationSet
        && reader.GetGuid(20) = value.SignerOneId
        && reader.GetGuid(21) = value.SignerTwoId
        && reader.GetGuid(22) = value.OwnerOneActorId
        && reader.GetGuid(23) = value.OwnerTwoActorId
        && reader.GetInt64(24) = value.OwnerOneGrantRevision
        && reader.GetInt64(25) = value.OwnerTwoGrantRevision
        && reader.GetInt64(26) = value.AuthorityRevision
        && reader.GetFieldValue<DateTimeOffset>(27) = value.ValidUntil

    let receipt
        (connection: NpgsqlConnection)
        transaction
        (row: LossRetirementEvidence)
        value
        requirePresent
        =
        use count =
            new NpgsqlCommand(
                "SELECT count(*) FROM claimcore.installation_loss_retirements",
                connection,
                transaction
            )

        let total = count.ExecuteScalar() :?> int64

        if total > 1L then
            corrupt ()

        use command =
            new NpgsqlCommand(
                "SELECT r.canonical_decision,r.canonical_sha256,r.signature_one,r.signature_two,"
                + "r.previous_sequence,r.previous_hash,r.witness_intent_sequence,"
                + "r.witness_intent_hash,r.known_operation_count,r.known_operation_digest,"
                + "l.loss_retired,l.loss_retirement_id,l.loss_retirement_intent_sequence,"
                + "l.loss_retirement_intent_hash,r.installation_id,r.lineage_id,r.old_epoch,"
                + "r.evidence_report_sha256,r.independent_checkpoint_sha256,r.operation_set_kind,"
                + "r.signer_one_id,r.signer_two_id,r.owner_one_actor_id,r.owner_two_actor_id,"
                + "r.owner_one_grant_revision,r.owner_two_grant_revision,r.authority_revision,"
                + "r.valid_until FROM claimcore.installation_loss_retirements r "
                + "JOIN claimcore.installation_lineage l ON l.singleton "
                + "WHERE r.retirement_id=@retirement",
                connection,
                transaction
            )

        Sql.uuid command "retirement" row.RetirementId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            if requirePresent || total <> 0L then
                corrupt ()

            false
        else
            if not (receiptMatches reader row && decisionMatches reader value) || reader.Read() then
                corrupt ()

            true

    let denials (connection: NpgsqlConnection) transaction (row: LossRetirementEvidence) =
        use command =
            new NpgsqlCommand(
                "SELECT operation_commitment FROM claimcore.installation_loss_operation_denials "
                + "WHERE retirement_id=@retirement ORDER BY operation_commitment",
                connection,
                transaction
            )

        Sql.uuid command "retirement" row.RetirementId
        use reader = command.ExecuteReader()
        let values = ResizeArray<byte array>()

        while reader.Read() do
            if values.Count >= 10000 then
                corrupt ()

            values.Add(reader.GetFieldValue<byte array>(0))

        if
            values.Count <> row.KnownOperationCount
            || InstallationLossOperationCommitments.digestOfCommitments values
               <> row.KnownOperationDigest
        then
            corrupt ()
