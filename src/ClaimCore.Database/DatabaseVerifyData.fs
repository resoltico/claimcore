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

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal VerifyDataOutcome =
    | Verified of DataAuditSummary * Snapshot
    | InputRefused of DatabaseInputProblem
    | AuditFailed

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

    let private auditedUsing
        (ownerConnection: string)
        (custody: IKeyCustody)
        (key: SuppressionKeyFile)
        (openStore: Identity -> Store)
        inspect
        =
        let builder = OwnerConnection.builder ownerConnection
        use barrier = new NpgsqlConnection(builder.ConnectionString)
        barrier.Open()
        OwnerConnection.requireIdentity barrier
        SchemaBaseline.requireCurrent barrier
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

        witness.AdmitReadOnly()
        use auditConnection = new NpgsqlConnection(builder.ConnectionString)
        auditConnection.Open()
        OwnerConnection.requireIdentity auditConnection
        SchemaBaseline.requireCurrent auditConnection
        DatabaseEnvironment.requireCompatible auditConnection

        let summary =
            DataAudit.runWithSuppression auditConnection witness (Some port) CancellationToken.None
            |> fun work -> work.GetAwaiter().GetResult()

        let tip = witness.Snapshot()

        if tip.TipSequence <> summary.WitnessCutoff then
            invalidOp "Witness tip moved during the audit."

        let inspected = inspect barrier transaction witness summary tip
        transaction.Rollback()
        summary, tip, inspected

    let internal auditedWith ownerConnection witnessConnection custody key inspect =
        use capability =
            DatabaseWitnessInputs.writerCapability ()
            |> Result.defaultWith (fun _ -> invalidOp "Private writer capability is unavailable.")

        auditedUsing
            ownerConnection
            custody
            key
            (fun installation ->
                capability.Use(fun material ->
                    new Store(witnessConnection, installation, material)))
            inspect

    let internal auditedRestoredWith
        ownerConnection
        (witnessAuditConnection: string)
        custody
        key
        inspect
        =
        let witness = NpgsqlConnectionStringBuilder(witnessAuditConnection)

        if witness.Username <> "claimcore_witness_auditor" then
            invalidOp "Restored-pair witness audit role is unavailable."

        auditedUsing
            ownerConnection
            custody
            key
            (fun installation -> Store.OpenAudit(witnessAuditConnection, installation))
            inspect

    let private audited ownerConnection witnessConnection custody key =
        let summary, tip, _ =
            auditedWith ownerConnection witnessConnection custody key (fun _ _ _ _ _ -> ())

        summary, tip

    let run ownerConnection =
        match DatabaseWitnessInputs.witnessWriterConnection () with
        | Error reason -> VerifyDataOutcome.InputRefused reason
        | Ok witnessConnection ->
            match DatabaseWitnessInputs.keyRing () with
            | Error reason -> VerifyDataOutcome.InputRefused reason
            | Ok custody ->
                use custody = custody

                match Environment.GetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE") with
                | null
                | "" ->
                    VerifyDataOutcome.InputRefused DatabaseInputProblem.SuppressionKeyFileRefused
                | keyPath ->
                    let loaded =
                        try
                            Some(SuppressionKeyFile.Load(keyPath))
                        with _ ->
                            None

                    match loaded with
                    | None ->
                        VerifyDataOutcome.InputRefused
                            DatabaseInputProblem.SuppressionKeyFileRefused
                    | Some key ->
                        use key = key

                        try
                            if not (separate ownerConnection witnessConnection) then
                                VerifyDataOutcome.AuditFailed
                            else
                                let summary, tip =
                                    audited ownerConnection witnessConnection custody key

                                VerifyDataOutcome.Verified(summary, tip)
                        with _ ->
                            VerifyDataOutcome.AuditFailed
