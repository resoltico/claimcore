namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal WriterHandoffAbortSigner =
    {
        KeyId: Guid
        Holder: Guid
        GrantRevision: int64
        PublicKey: byte array
        RegisteredSequence: int64
        RegisteredEventId: Guid
        RegisteredEpoch: int64
        RegisteredHash: byte array
        RegisteredCandidate: byte array
        RetiredSequence: int64 option
    }

/// The owner verifies key purpose, human holder and current OWNER grant before witnessed A1.
module internal WriterHandoffAbortApproval =
    let private query =
        "SELECT s.ed25519_public_key,s.active,s.signer_purpose,s.holder_actor_id,"
        + "a.principal_kind,a.enabled,g.active,g.changed_revision,"
        + "registered.witness_sequence,registered.event_id,"
        + "registered.witness_epoch,registered.witness_entry_hash,"
        + "registered.candidate_sha256,retired.witness_sequence "
        + "FROM claimcore.managed_copy_signers s "
        + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
        + "JOIN claimcore.actor_grants g ON g.actor_id=a.actor_id "
        + "AND g.scope_kind='INSTALLATION' "
        + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000'::uuid "
        + "AND g.role_name='OWNER' "
        + "JOIN claimcore.managed_copy_signer_events registered "
        + "ON registered.signing_key_id=s.signing_key_id AND registered.revision=1 "
        + "LEFT JOIN claimcore.managed_copy_signer_events retired "
        + "ON retired.signing_key_id=s.signing_key_id AND retired.revision=2 "
        + "WHERE s.signing_key_id=@key FOR SHARE OF s,a,g"

    let current connection transaction keyId =
        use command = new NpgsqlCommand(query, connection, transaction)

        Sql.uuid command "key" keyId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Abort owner signing key is absent."

        let key = reader.GetFieldValue<byte array>(0)
        let active = reader.GetBoolean(1)
        let purpose = reader.GetString(2)
        let holder = reader.GetGuid(3)
        let human = reader.GetString(4) = "HUMAN"
        let enabled = reader.GetBoolean(5)
        let owner = reader.GetBoolean(6)
        let grantRevision = reader.GetInt64(7)
        let registered = reader.GetInt64(8)
        let eventId = reader.GetGuid(9)
        let epoch = reader.GetInt64(10)
        let hash = reader.GetFieldValue<byte array>(11)
        let candidate = reader.GetFieldValue<byte array>(12)

        let retired =
            if reader.IsDBNull(13) then
                None
            else
                Some(reader.GetInt64(13))

        if
            reader.Read()
            || key.Length <> 32
            || not active
            || not human
            || not enabled
            || not owner
            || purpose <> "WRITER_HANDOFF_ABORT"
        then
            invalidOp "Abort owner signing key is unavailable."

        {
            KeyId = keyId
            Holder = holder
            GrantRevision = grantRevision
            PublicKey = key
            RegisteredSequence = registered
            RetiredSequence = retired
            RegisteredEventId = eventId
            RegisteredEpoch = epoch
            RegisteredHash = hash
            RegisteredCandidate = candidate
        }

    let private holdersMatch
        (first: WriterHandoffAbortSigner)
        (second: WriterHandoffAbortSigner)
        (value: WriterHandoffAbort)
        =
        first.Holder <> second.Holder
        && first.Holder = value.OwnerOneActorId
        && second.Holder = value.OwnerTwoActorId
        && first.GrantRevision = value.OwnerOneGrantRevision
        && second.GrantRevision = value.OwnerTwoGrantRevision

    let private signaturesMatch
        (first: WriterHandoffAbortSigner)
        (second: WriterHandoffAbortSigner)
        (value: WriterHandoffAbort)
        (canonical: byte array)
        (signatureOne: byte array)
        (signatureTwo: byte array)
        =
        first.RegisteredSequence < value.PrepareSequence
        && second.RegisteredSequence < value.PrepareSequence
        && first.RetiredSequence.IsNone
        && second.RetiredSequence.IsNone
        && signatureOne.Length = 64
        && signatureTwo.Length = 64
        && ManagedCopySignature.verify first.PublicKey canonical signatureOne
        && ManagedCopySignature.verify second.PublicKey canonical signatureTwo

    let verifyCurrent
        connection
        transaction
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        (canonical: byte array)
        (signatureOne: byte array)
        (signatureTwo: byte array)
        =
        let first = current connection transaction value.AbortSigningKeyOneId
        let second = current connection transaction value.AbortSigningKeyTwoId

        if
            not (holdersMatch first second value)
            || not (signaturesMatch first second value canonical signatureOne signatureTwo)
        then
            invalidOp "Two independent current abort owner signatures are unavailable."

        for signer in [ first; second ] do
            witness.VerifyAuthorityEvidenceForInstallation(
                signer.RegisteredEventId,
                signer.RegisteredSequence,
                signer.RegisteredEpoch,
                signer.RegisteredHash,
                signer.RegisteredCandidate
            )

        first, second
