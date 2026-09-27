namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open WitnessProtocolReconciliation

/// Exact immutable A1 readback; a witnessed ABORT is still quarantined until A2/A3.
module internal WriterHandoffOwnerAbortEvidence =
    [<NoEquality; NoComparison>]
    type private HistoricalSignerRow =
        {
            PublicKey: byte array
            Purpose: string
            Holder: Guid
            Human: bool
            EventId: Guid
            Registration: int64
            Epoch: int64
            Hash: byte array
            Candidate: byte array
            Retired: int64 option
            Extra: bool
        }

    let private identityMatches
        (witness: WitnessProtocol)
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffAbort)
        (entry: WriterHandoffEvidence)
        =
        entry.HandoffId = value.HandoffId
        && value.InstallationId = witness.Identity.InstallationId
        && value.LineageId = witness.Identity.LineageId
        && value.Epoch = witness.Identity.Epoch
        && value.OldGeneration = prepared.Value.OldGeneration
        && value.NewCapabilitySha256 = prepared.Value.NewCapabilitySha256
        && value.PrepareCanonicalSha256 = SHA256.HashData(prepared.Canonical)

    let private preparationMatches
        (prepared: PrimaryWriterPreparation)
        (entry: WriterHandoffEvidence)
        sequence
        =
        entry.PrepareSequence = prepared.Intent.Sequence
        && entry.PrepareHash = prepared.Intent.EntryHash
        && entry.PrepareCanonical = prepared.Canonical
        && entry.PrepareSignature = prepared.Signature
        && entry.SettlementSequence.IsNone
        && sequence = prepared.Intent.Sequence + 1L

    let private signedAbortMatches
        (value: WriterHandoffAbort)
        (entry: WriterHandoffEvidence)
        digest
        canonical
        signatureOne
        signatureTwo
        =
        entry.AbortCandidateSha256 = Some digest
        && entry.AbortCanonical = Some canonical
        && entry.AbortSignatureOne = Some signatureOne
        && entry.AbortSignatureTwo = Some signatureTwo
        && entry.AbortSigningKeyOne = Some value.AbortSigningKeyOneId
        && entry.AbortSigningKeyTwo = Some value.AbortSigningKeyTwoId

    let private exactEntry
        (witness: WitnessProtocol)
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        (entry: WriterHandoffEvidence)
        =
        let digest =
            WriterHandoffWitnessAbortCommands.candidate canonical signatureOne signatureTwo

        let sequence =
            entry.AbortSequence
            |> Option.defaultWith (fun () -> invalidOp "Abort A1 is absent.")

        let hash =
            entry.AbortHash
            |> Option.defaultWith (fun () -> invalidOp "Abort A1 hash is absent.")

        if
            not (identityMatches witness prepared value entry)
            || not (preparationMatches prepared entry sequence)
            || not (signedAbortMatches value entry digest canonical signatureOne signatureTwo)
        then
            invalidOp "Witness abort differs from signed owner candidate."

        let evidence =
            witness.EvidenceStore.TryReadEvidence(value.HandoffId, AbortedBeforeCommit)
            |> Option.defaultWith (fun () -> invalidOp "Witness abort journal evidence is absent.")

        if
            evidence.Ticket.Sequence <> sequence
            || evidence.Ticket.EntryHash <> hash
            || evidence.Ticket.ScopeKind <> Installation
        then
            invalidOp "Witness abort journal ticket differs."

        witness.VerifyHistoricalTip(sequence, hash)

        let plain =
            witness.KeyCustody.Decrypt(
                evidence.Ticket.KeyId,
                witness.AssociatedData(value.HandoffId, "ABORTED_BEFORE_COMMIT"),
                evidence.EncryptedPayload
            )

        try
            if plain <> digest then
                invalidOp "Witness abort ciphertext differs."
        finally
            CryptographicOperations.ZeroMemory(plain)
            CryptographicOperations.ZeroMemory(digest)

        evidence.Ticket

    let read
        (witness: WitnessProtocol)
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        =
        match witness.EvidenceStore.TryReadHandoff(value.HandoffId) with
        | Some entry when entry.AbortSequence.IsSome ->
            Some(exactEntry witness prepared value canonical signatureOne signatureTwo entry)
        | _ -> None

    let private readHistoricalSigner connection transaction keyId =
        use command =
            new NpgsqlCommand(
                "SELECT s.ed25519_public_key,s.signer_purpose,s.holder_actor_id,"
                + "a.principal_kind,r.event_id,r.witness_sequence,r.witness_epoch,"
                + "r.witness_entry_hash,r.candidate_sha256,retired.witness_sequence "
                + "FROM claimcore.managed_copy_signers s "
                + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
                + "JOIN claimcore.managed_copy_signer_events r "
                + "ON r.signing_key_id=s.signing_key_id AND r.revision=1 "
                + "LEFT JOIN claimcore.managed_copy_signer_events retired "
                + "ON retired.signing_key_id=s.signing_key_id AND retired.revision=2 "
                + "WHERE s.signing_key_id=@key",
                connection,
                transaction
            )

        Sql.uuid command "key" keyId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Historical abort signer is absent."

        let row =
            {
                PublicKey = reader.GetFieldValue<byte array>(0)
                Purpose = reader.GetString(1)
                Holder = reader.GetGuid(2)
                Human = reader.GetString(3) = "HUMAN"
                EventId = reader.GetGuid(4)
                Registration = reader.GetInt64(5)
                Epoch = reader.GetInt64(6)
                Hash = reader.GetFieldValue<byte array>(7)
                Candidate = reader.GetFieldValue<byte array>(8)
                Retired =
                    if reader.IsDBNull(9) then
                        None
                    else
                        Some(reader.GetInt64(9))
                Extra = false
            }

        let extra = reader.Read()
        reader.Close()
        { row with Extra = extra }

    let private historicalSigner
        connection
        transaction
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        keyId
        actor
        grantRevision
        canonical
        signature
        =
        task {
            let row = readHistoricalSigner connection transaction keyId

            if
                row.Extra
                || row.Purpose <> "WRITER_HANDOFF_ABORT"
                || row.Holder <> actor
                || not row.Human
                || row.PublicKey.Length <> 32
                || row.Registration >= value.PrepareSequence
                || (row.Retired |> Option.exists (fun seq -> seq <= value.PrepareSequence + 1L))
                || not (ManagedCopySignature.verify row.PublicKey canonical signature)
            then
                invalidOp "Historical abort signer differs."

            witness.VerifyAuthorityEvidenceForInstallation(
                row.EventId,
                row.Registration,
                row.Epoch,
                row.Hash,
                row.Candidate
            )

            do!
                DataAuditHandoffOwnerRole.verify
                    connection
                    transaction
                    actor
                    value.ExpectedAuthorityRevision
                    CancellationToken.None

            do!
                WriterHandoffOwnerAbortGrantEvidence.verifyGrantEvent
                    connection
                    transaction
                    actor
                    grantRevision
        }


    let verifyHistoricalOwners
        connection
        transaction
        witness
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        =
        task {
            do!
                historicalSigner
                    connection
                    transaction
                    witness
                    value
                    value.AbortSigningKeyOneId
                    value.OwnerOneActorId
                    value.OwnerOneGrantRevision
                    canonical
                    signatureOne

            do!
                historicalSigner
                    connection
                    transaction
                    witness
                    value
                    value.AbortSigningKeyTwoId
                    value.OwnerTwoActorId
                    value.OwnerTwoGrantRevision
                    canonical
                    signatureTwo
        }
