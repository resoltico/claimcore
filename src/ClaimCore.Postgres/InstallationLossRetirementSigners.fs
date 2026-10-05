namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal InstallationLossSigner =
    {
        KeyId: Guid
        Holder: Guid
        GrantRevision: int64
        PublicKey: byte array
        RegisteredEventId: Guid
        RegisteredSequence: int64
        RegisteredEpoch: int64
        RegisteredHash: byte array
        RegisteredCandidate: byte array
    }

/// Only two distinct, currently granted human owners holding purpose-specific keys
/// may authorize the exact signed terminal decision.
module internal InstallationLossRetirementSigners =
    let private query (connection: NpgsqlConnection) transaction keyId =
        let command =
            new NpgsqlCommand(
                "SELECT s.ed25519_public_key,s.active,s.signer_purpose,s.holder_actor_id,"
                + "a.principal_kind,a.enabled,g.active,g.changed_revision,"
                + "r.event_id,r.witness_sequence,r.witness_epoch,r.witness_entry_hash,"
                + "r.candidate_sha256,retired.witness_sequence "
                + "FROM claimcore.managed_copy_signers s "
                + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
                + "JOIN claimcore.actor_grants g ON g.actor_id=a.actor_id "
                + "AND g.scope_kind='INSTALLATION' "
                + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000'::uuid "
                + "AND g.role_name='OWNER' "
                + "JOIN claimcore.managed_copy_signer_events r "
                + "ON r.signing_key_id=s.signing_key_id AND r.revision=1 "
                + "LEFT JOIN claimcore.managed_copy_signer_events retired "
                + "ON retired.signing_key_id=s.signing_key_id AND retired.revision=2 "
                + "WHERE s.signing_key_id=@key FOR SHARE OF s,a,g",
                connection,
                transaction
            )

        Sql.uuid command "key" keyId
        command

    let private decode keyId (reader: System.Data.Common.DbDataReader) =
        if not (reader.HasRows) then
            invalidOp "Loss owner signing key is absent."

        let publicKey = reader.GetFieldValue<byte array>(0)
        let active = reader.GetBoolean(1)
        let purpose = reader.GetString(2)
        let holder = reader.GetGuid(3)
        let human = reader.GetString(4) = "HUMAN"
        let enabled = reader.GetBoolean(5)
        let owner = reader.GetBoolean(6)
        let grantRevision = reader.GetInt64(7)
        let registeredEventId = reader.GetGuid(8)
        let registeredSequence = reader.GetInt64(9)
        let registeredEpoch = reader.GetInt64(10)
        let registeredHash = reader.GetFieldValue<byte array>(11)
        let registeredCandidate = reader.GetFieldValue<byte array>(12)
        let retired = not (reader.IsDBNull(13))

        if
            not active
            || retired
            || not human
            || not enabled
            || not owner
            || purpose <> "INSTALLATION_LOSS_RETIREMENT"
            || publicKey.Length <> 32
        then
            invalidOp "Loss owner signing key is unavailable."

        let value =
            {
                KeyId = keyId
                Holder = holder
                GrantRevision = grantRevision
                PublicKey = publicKey
                RegisteredEventId = registeredEventId
                RegisteredSequence = registeredSequence
                RegisteredEpoch = registeredEpoch
                RegisteredHash = registeredHash
                RegisteredCandidate = registeredCandidate
            }

        value

    let private readCurrent
        (connection: NpgsqlConnection)
        transaction
        keyId
        (ct: CancellationToken)
        =
        task {
            use command = query connection transaction keyId
            use! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync(ct)

            if not found then
                invalidOp "Loss owner signing key is absent."

            let value = decode keyId reader
            let! duplicated = reader.ReadAsync(ct)

            if duplicated then
                invalidOp "Loss owner signing key is duplicated."

            return value
        }

    let pair connection transaction (witness: WitnessProtocol) firstKey secondKey ct =
        task {
            if firstKey = Guid.Empty || secondKey = Guid.Empty || firstKey = secondKey then
                invalidOp "Two distinct loss owner signing keys are required."

            let! first = readCurrent connection transaction firstKey ct
            let! second = readCurrent connection transaction secondKey ct

            if first.Holder = second.Holder then
                invalidOp "Loss retirement requires two distinct human owners."

            for signer in [ first; second ] do
                do!
                    witness.VerifyAuthorityEvidenceForInstallation(
                        signer.RegisteredEventId,
                        signer.RegisteredSequence,
                        signer.RegisteredEpoch,
                        signer.RegisteredHash,
                        signer.RegisteredCandidate,
                        ct
                    )

            return first, second
        }

    /// Real-data activation must prequalify this owner-only incident lane while the
    /// complete actor/grant authority chain is still independently auditable.
    let requireReady
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        (ct: CancellationToken)
        =
        task {
            let! projection =
                DataAuditWitness.verifyAuthorityEvents connection transaction witness cutoff ct

            let! _ = DataAuditAuthorityProjection.verify connection transaction projection ct

            use command =
                new NpgsqlCommand(
                    "SELECT s.signing_key_id,s.holder_actor_id "
                    + "FROM claimcore.managed_copy_signers s "
                    + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
                    + "JOIN claimcore.actor_grants g ON g.actor_id=a.actor_id "
                    + "AND g.scope_kind='INSTALLATION' "
                    + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000'::uuid "
                    + "AND g.role_name='OWNER' AND g.active "
                    + "WHERE s.signer_purpose='INSTALLATION_LOSS_RETIREMENT' AND s.active "
                    + "AND a.enabled AND a.principal_kind='HUMAN' ORDER BY s.signing_key_id LIMIT 1001",
                    connection,
                    transaction
                )

            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<Guid * Guid>()

            while! reader.ReadAsync(ct) do
                rows.Add(reader.GetGuid(0), reader.GetGuid(1))

            if rows.Count > 1000 then
                invalidOp "Loss owner signer roster exceeds its bound."

            let first =
                rows
                |> Seq.tryHead
                |> Option.defaultWith (fun () ->
                    invalidOp "Loss owner signer roster is incomplete.")

            let second =
                rows
                |> Seq.tryFind (fun (_, holder) -> holder <> snd first)
                |> Option.defaultWith (fun () ->
                    invalidOp "Two independent loss owners are unavailable.")

            reader.Close()
            let! _ = pair connection transaction witness (fst first) (fst second) ct
            return ()
        }

    let verify
        (first: InstallationLossSigner)
        (second: InstallationLossSigner)
        (value: InstallationLossRetirementDecision)
        canonical
        signatureOne
        signatureTwo
        =
        first.Holder = value.OwnerOneActorId
        && second.Holder = value.OwnerTwoActorId
        && first.KeyId = value.SignerOneId
        && second.KeyId = value.SignerTwoId
        && first.GrantRevision = value.OwnerOneGrantRevision
        && second.GrantRevision = value.OwnerTwoGrantRevision
        && first.RegisteredSequence <= value.PreviousSequence
        && second.RegisteredSequence <= value.PreviousSequence
        && ManagedCopySignature.verify first.PublicKey canonical signatureOne
        && ManagedCopySignature.verify second.PublicKey canonical signatureTwo
