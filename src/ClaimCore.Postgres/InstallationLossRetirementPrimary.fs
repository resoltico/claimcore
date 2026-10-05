namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

/// The primary is a receipt and permanent operation-identity suppression projection;
/// the independent witness W0 remains the immediate terminal safety fence.
module internal InstallationLossRetirementPrimary =
    let private uuid (command: NpgsqlCommand) name value = Sql.uuid command name value
    let private number (command: NpgsqlCommand) name value = Sql.integer command name value

    let private bytes (command: NpgsqlCommand) name value =
        Sql.add command name NpgsqlDbType.Bytea (box value)

    let private optionalBytes (command: NpgsqlCommand) name value =
        Sql.add
            command
            name
            NpgsqlDbType.Bytea
            (value |> Option.map box |> Option.defaultValue DBNull.Value)

    let private bindReceipt
        (command: NpgsqlCommand)
        (value: InstallationLossRetirementDecision)
        (canonical: byte array)
        signatureOne
        signatureTwo
        (intent: Ticket)
        =
        uuid command "id" value.RetirementId
        uuid command "installation" value.InstallationId
        uuid command "lineage" value.LineageId
        number command "epoch" value.Epoch
        number command "previousSequence" value.PreviousSequence
        bytes command "previousHash" value.PreviousHash
        optionalBytes command "report" value.EvidenceReportSha256
        optionalBytes command "checkpoint" value.IndependentCheckpointSha256

        Sql.text
            command
            "setKind"
            (InstallationLossRetirementCandidate.operationSetName value.OperationSet)

        Sql.add command "count" NpgsqlDbType.Integer (box value.KnownOperationCount)
        bytes command "digest" value.KnownOperationDigest
        uuid command "firstKey" value.SignerOneId
        uuid command "secondKey" value.SignerTwoId
        uuid command "firstOwner" value.OwnerOneActorId
        uuid command "secondOwner" value.OwnerTwoActorId
        number command "firstRevision" value.OwnerOneGrantRevision
        number command "secondRevision" value.OwnerTwoGrantRevision
        number command "authorityRevision" value.AuthorityRevision
        Sql.add command "validUntil" NpgsqlDbType.TimestampTz (box value.ValidUntil)
        bytes command "canonical" canonical
        bytes command "canonicalHash" (SHA256.HashData(canonical))
        bytes command "firstSignature" signatureOne
        bytes command "secondSignature" signatureTwo
        number command "intentSequence" intent.Sequence
        bytes command "intentHash" intent.EntryHash

    let private insertDenials connection transaction retirementId commitments =
        for commitment in commitments do
            use denial =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.installation_loss_operation_denials "
                    + "(operation_commitment,retirement_id) VALUES (@commitment,@retirement)",
                    connection,
                    transaction
                )

            bytes denial "commitment" commitment
            uuid denial "retirement" retirementId

            if denial.ExecuteNonQuery() <> 1 then
                invalidOp "Loss operation denial was not retained."

    let private advanceLineage
        connection
        transaction
        (value: InstallationLossRetirementDecision)
        (intent: Ticket)
        =
        use lineage =
            new NpgsqlCommand(
                "UPDATE claimcore.installation_lineage SET loss_retired=true,"
                + "loss_retirement_id=@retirement,loss_retirement_intent_sequence=@sequence,"
                + "loss_retirement_intent_hash=@hash "
                + "WHERE singleton AND installation_id=@installation AND lineage_id=@lineage "
                + "AND witness_epoch=@epoch AND NOT loss_retired",
                connection,
                transaction
            )

        uuid lineage "retirement" value.RetirementId
        number lineage "sequence" intent.Sequence
        bytes lineage "hash" intent.EntryHash
        uuid lineage "installation" value.InstallationId
        uuid lineage "lineage" value.LineageId
        number lineage "epoch" value.Epoch

        if lineage.ExecuteNonQuery() <> 1 then
            invalidOp "Loss primary retirement projection diverged."

    let insert
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: InstallationLossRetirementDecision)
        (canonical: byte array)
        signatureOne
        signatureTwo
        (intent: Ticket)
        (commitments: byte array list)
        =
        use command =
            new NpgsqlCommand(
                "INSERT INTO claimcore.installation_loss_retirements ("
                + "retirement_id,installation_id,lineage_id,old_epoch,previous_sequence,previous_hash,"
                + "evidence_report_sha256,independent_checkpoint_sha256,operation_set_kind,"
                + "known_operation_count,known_operation_digest,signer_one_id,signer_two_id,"
                + "owner_one_actor_id,owner_two_actor_id,owner_one_grant_revision,"
                + "owner_two_grant_revision,authority_revision,valid_until,canonical_decision,"
                + "canonical_sha256,signature_one,signature_two,witness_intent_sequence,"
                + "witness_intent_hash) VALUES ("
                + "@id,@installation,@lineage,@epoch,@previousSequence,@previousHash,"
                + "@report,@checkpoint,@setKind,@count,@digest,@firstKey,@secondKey,"
                + "@firstOwner,@secondOwner,@firstRevision,@secondRevision,@authorityRevision,"
                + "@validUntil,@canonical,@canonicalHash,@firstSignature,@secondSignature,"
                + "@intentSequence,@intentHash)",
                connection,
                transaction
            )

        bindReceipt command value canonical signatureOne signatureTwo intent

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Loss primary receipt was not retained."

        insertDenials connection transaction value.RetirementId commitments
        advanceLineage connection transaction value intent

    let private readCommand connection transaction sql =
        match transaction with
        | Some(barrier: NpgsqlTransaction) -> new NpgsqlCommand(sql, connection, barrier)
        | None -> new NpgsqlCommand(sql, connection)

    let private receiptExact
        (connection: NpgsqlConnection)
        transaction
        (value: InstallationLossRetirementDecision)
        (canonical: byte array)
        signatureOne
        signatureTwo
        (intent: Ticket)
        =
        use command =
            readCommand
                connection
                transaction
                ("SELECT r.canonical_decision,r.canonical_sha256,r.signature_one,r.signature_two,"
                 + "r.witness_intent_sequence,r.witness_intent_hash,r.known_operation_count,"
                 + "r.known_operation_digest,l.loss_retired,l.loss_retirement_id,"
                 + "l.loss_retirement_intent_sequence,l.loss_retirement_intent_hash "
                 + "FROM claimcore.installation_loss_retirements r "
                 + "JOIN claimcore.installation_lineage l ON l.singleton "
                 + "WHERE r.retirement_id=@retirement")

        uuid command "retirement" value.RetirementId
        use reader = command.ExecuteReader()

        let valid =
            reader.Read()
            && reader.GetFieldValue<byte array>(0) = canonical
            && reader.GetFieldValue<byte array>(1) = SHA256.HashData(canonical)
            && reader.GetFieldValue<byte array>(2) = signatureOne
            && reader.GetFieldValue<byte array>(3) = signatureTwo
            && reader.GetInt64(4) = intent.Sequence
            && reader.GetFieldValue<byte array>(5) = intent.EntryHash
            && reader.GetInt32(6) = value.KnownOperationCount
            && reader.GetFieldValue<byte array>(7) = value.KnownOperationDigest
            && reader.GetBoolean(8)
            && reader.GetGuid(9) = value.RetirementId
            && reader.GetInt64(10) = intent.Sequence
            && reader.GetFieldValue<byte array>(11) = intent.EntryHash
            && not (reader.Read())

        reader.Close()

        valid

    let private denialsExact
        (connection: NpgsqlConnection)
        transaction
        retirementId
        (commitments: byte array list)
        =
        use count =
            readCommand
                connection
                transaction
                ("SELECT count(*) FROM claimcore.installation_loss_operation_denials "
                 + "WHERE retirement_id=@retirement")

        uuid count "retirement" retirementId
        let observed = count.ExecuteScalar() :?> int64

        if observed <> int64 commitments.Length then
            false
        else
            commitments
            |> List.forall (fun commitment ->
                use denial =
                    readCommand
                        connection
                        transaction
                        ("SELECT EXISTS (SELECT 1 FROM claimcore.installation_loss_operation_denials "
                         + "WHERE operation_commitment=@commitment AND retirement_id=@retirement)")

                bytes denial "commitment" commitment
                uuid denial "retirement" retirementId
                denial.ExecuteScalar() :?> bool)

    let private exactIn
        transaction
        connection
        value
        canonical
        signatureOne
        signatureTwo
        intent
        commitments
        =
        receiptExact connection transaction value canonical signatureOne signatureTwo intent
        && denialsExact connection transaction value.RetirementId commitments

    let exact connection value canonical signatureOne signatureTwo intent commitments =
        exactIn None connection value canonical signatureOne signatureTwo intent commitments

    let exactLocked
        connection
        barrier
        value
        canonical
        signatureOne
        signatureTwo
        intent
        commitments
        =
        exactIn
            (Some barrier)
            connection
            value
            canonical
            signatureOne
            signatureTwo
            intent
            commitments

    let commitReceipt
        (primaryOwner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        alreadyRetired
        decision
        canonical
        signatureOne
        signatureTwo
        intent
        commitments
        =
        task {
            if alreadyRetired then
                do! transaction.RollbackAsync(CancellationToken.None)
            else
                insert
                    primaryOwner
                    transaction
                    decision
                    canonical
                    signatureOne
                    signatureTwo
                    intent
                    commitments

                do! transaction.CommitAsync(CancellationToken.None)
        }
