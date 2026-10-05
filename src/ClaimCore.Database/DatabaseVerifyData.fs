namespace ClaimCore.Database

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Owner-only, read-only full audit. A held primary authority row prevents product mutations
/// while the stable primary snapshot is reconciled with one independent witness cutoff.
module internal DatabaseVerifyData =
    let identity (connection: NpgsqlConnection) =
        use command =
            new NpgsqlCommand(
                "SELECT installation_id,lineage_id,witness_epoch,suppression_key_id,"
                + "suppression_key_check FROM claimcore.installation_lineage WHERE singleton",
                connection
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Installation identity is absent."

        let identity: Identity =
            {
                InstallationId = reader.GetGuid(0)
                LineageId = reader.GetGuid(1)
                Epoch = reader.GetInt64(2)
            }

        let keyId = reader.GetGuid(3)
        let check = reader.GetFieldValue<byte array>(4)

        if reader.Read() then
            invalidOp "Installation identity is ambiguous."

        identity, keyId, check

    let commitments
        (key: SuppressionKeyFile)
        (identity: Identity)
        expectedKeyId
        (expectedCheck: byte array)
        =
        let admit () =
            if key.KeyId <> expectedKeyId then
                invalidOp "Suppression key identity differs."

            let actual = key.Check(identity.InstallationId, identity.LineageId)

            if not (CryptographicOperations.FixedTimeEquals(actual, expectedCheck)) then
                invalidOp "Suppression key check differs."

        admit ()

        { new ISuppressionCommitments with
            member _.InstallationId = identity.InstallationId
            member _.LineageId = identity.LineageId
            member _.KeyId = key.KeyId
            member _.Admit() = admit ()

            member _.Reference(reference) =
                key.Reference(identity.InstallationId, identity.LineageId, reference)

            member _.Operation(operationId) =
                key.Operation(identity.InstallationId, identity.LineageId, operationId)

            member _.RequestCandidate(canonical) =
                key.RequestCandidate(identity.InstallationId, identity.LineageId, canonical)

            member _.PurgeProposal(canonicalDraft) =
                key.PurgeProposal(identity.InstallationId, identity.LineageId, canonicalDraft)

            member _.ApprovalDraft(canonicalDraft) =
                key.ApprovalDraft(identity.InstallationId, identity.LineageId, canonicalDraft)

            member _.ApprovalCanonical(canonicalApproval) =
                key.ApprovalCanonical(
                    identity.InstallationId,
                    identity.LineageId,
                    canonicalApproval
                )
        }

    let private separate (ownerConnection: string) (witnessConnection: string) =
        let primary = OwnerConnection.builder ownerConnection
        let witness = NpgsqlConnectionStringBuilder(witnessConnection)

        witness.Username = "claimcore_witness_writer"
        && not (
            String.Equals(primary.Host, witness.Host, StringComparison.OrdinalIgnoreCase)
            && primary.Port = witness.Port
        )

    // The audit owns its transient protocol; the caller owns and disposes the key ring.
    let private borrowedCustody (custody: IKeyCustody) =
        { new IKeyCustody with
            member _.ActiveKeyId = custody.ActiveKeyId
            member _.HasKey keyId = custody.HasKey keyId

            member _.Encrypt(keyId, associatedData, plaintext) =
                custody.Encrypt(keyId, associatedData, plaintext)

            member _.Decrypt(keyId, associatedData, envelope) =
                custody.Decrypt(keyId, associatedData, envelope)

            member _.Dispose() = ()
        }

    let private auditSnapshot
        (connectionString: string)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (before: Snapshot)
        =
        task {
            use auditConnection = new NpgsqlConnection(connectionString)
            auditConnection.Open()
            OwnerConnection.requireIdentity auditConnection
            SchemaBaseline.requireCurrent auditConnection
            DatabaseEnvironment.requireCompatible auditConnection

            let! summary =
                DataAudit.runWithSuppression
                    auditConnection
                    witness
                    (Some commitments)
                    CancellationToken.None

            let! tip = witness.Snapshot(CancellationToken.None)

            if
                tip.TipSequence <> summary.WitnessCutoff
                || tip.TipSequence <> before.TipSequence
            then
                invalidOp "Witness tip moved during the audit."

            return summary, tip
        }

    let private witnessFence (witness: WitnessProtocol) requireWriterFence =
        task {
            let! before = witness.Snapshot(CancellationToken.None)

            // Pending owner phases already fence ordinary appends. The outer session and
            // primary row locks drain allowed owner transitions before the cutoff is captured.
            if
                requireWriterFence
                && not (
                    before.HandoffPending
                    || before.ActivationPending
                    || before.LossRetirementPending
                    || before.LossRetired
                )
            then
                return! witness.AcquireReadFence(before.WriterGeneration, CancellationToken.None)
            else
                return
                    { new IDisposable with
                        member _.Dispose() = ()
                    }
        }

    let private auditedUsing
        (ownerConnection: string)
        (custody: IKeyCustody)
        (key: SuppressionKeyFile)
        (openStore: Identity -> Store)
        requireWriterFence
        inspect
        =
        task {
            let builder = OwnerConnection.builder ownerConnection
            use barrier = new NpgsqlConnection(builder.ConnectionString)
            barrier.Open()
            OwnerConnection.requireIdentity barrier
            SchemaBaseline.requireCurrent barrier

            use! _operationFence =
                AuthorityOperationFence.acquireExclusive None barrier CancellationToken.None

            let installation, keyId, check = identity barrier
            use transaction = barrier.BeginTransaction(IsolationLevel.ReadCommitted)

            use lockCommand =
                new NpgsqlCommand(
                    "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR UPDATE",
                    barrier,
                    transaction
                )

            if isNull (lockCommand.ExecuteScalar()) then
                invalidOp "Authority barrier is unavailable."

            let port = commitments key installation keyId check

            let witnessStore = openStore installation

            use witness =
                new WitnessProtocol(witnessStore, borrowedCustody custody, installation)

            do! witness.AdmitReadOnly(CancellationToken.None)
            use! _fence = witnessFence witness requireWriterFence

            let! cutoff = witness.Snapshot(CancellationToken.None)
            let! summary, tip = auditSnapshot builder.ConnectionString witness port cutoff
            let! inspected = inspect barrier transaction witness summary tip
            do! transaction.RollbackAsync(CancellationToken.None)
            return summary, tip, inspected
        }

    let internal auditedWith ownerConnection witnessConnection custody key inspect =
        task {
            use capability =
                DatabaseWitnessInputs.writerCapability ()
                |> Result.defaultWith (fun _ ->
                    invalidOp "Private writer capability is unavailable.")

            return!
                auditedUsing
                    ownerConnection
                    custody
                    key
                    (fun installation ->
                        capability.Use(fun material ->
                            new Store(witnessConnection, installation, material)))
                    true
                    inspect
        }

    let internal auditedRestoredWith
        ownerConnection
        (witnessAuditConnection: string)
        custody
        key
        inspect
        =
        task {
            let witness = NpgsqlConnectionStringBuilder(witnessAuditConnection)

            if witness.Username <> "claimcore_witness_auditor" then
                invalidOp "Restored-pair witness audit role is unavailable."

            return!
                auditedUsing
                    ownerConnection
                    custody
                    key
                    (fun installation -> Store.OpenAudit(witnessAuditConnection, installation))
                    false
                    inspect
        }

    let private audited ownerConnection witnessConnection custody key =
        task {
            let! summary, tip, _ =
                auditedWith ownerConnection witnessConnection custody key (fun _ _ _ _ _ ->
                    System.Threading.Tasks.Task.FromResult(()))

            return summary, tip
        }

    let run ownerConnection =
        match DatabaseWitnessInputs.witnessWriterConnection () with
        | Error reason -> VerifyDataOutcome.InputRefused reason
        | Ok witnessConnection ->
            match DatabaseWitnessInputs.keyRing () with
            | Error reason -> VerifyDataOutcome.InputRefused reason
            | Ok custody ->
                use custody = custody

                match DatabaseWitnessInputs.suppressionKey () with
                | Error reason -> VerifyDataOutcome.InputRefused reason
                | Ok key ->
                    use key = key

                    try
                        if not (separate ownerConnection witnessConnection) then
                            VerifyDataOutcome.AuditFailed VerifyDataFailure.TopologyRefused
                        else
                            let summary, tip =
                                (audited ownerConnection witnessConnection custody key)
                                    .GetAwaiter()
                                    .GetResult()

                            VerifyDataOutcome.Verified(summary, tip)
                    with
                    | :? IO.InvalidDataException ->
                        VerifyDataOutcome.AuditFailed VerifyDataFailure.EvidenceDivergence
                    | :? NpgsqlException
                    | :? TimeoutException ->
                        VerifyDataOutcome.AuditFailed VerifyDataFailure.AuditUnavailable
                    | _ -> VerifyDataOutcome.AuditFailed VerifyDataFailure.AuditFault
