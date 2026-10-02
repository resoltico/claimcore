namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open WitnessProtocolReconciliation

/// Owner-only deletion transition; private inspection runs inside the held authority transaction.
module internal ManagedCopyVerifiedDeletion =
    let private holder (connection: NpgsqlConnection) transaction signingKeyId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT holder_actor_id FROM claimcore.managed_copy_signers "
                    + "WHERE signing_key_id=@key",
                    connection,
                    transaction
                )

            Sql.uuid command "key" signingKeyId
            let! result = command.ExecuteScalarAsync()

            return
                match result with
                | :? Guid as actor -> Some actor
                | _ -> None
        }

    let used (connection: NpgsqlConnection) transaction approvalId eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM claimcore.managed_copy_deletion_approval_uses "
                    + "WHERE approval_id=@approval AND deletion_event_id=@event)",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            Sql.uuid command "event" eventId
            let! value = command.ExecuteScalarAsync()
            return unbox<bool> value
        }

    let private exactCopy
        (witness: WitnessProtocol)
        (transition: ManagedCopyTransition)
        (current: ManagedCopyCurrent)
        (signer: (byte array * byte array * bool * CopySignerPurpose) option)
        (now: DateTimeOffset)
        (held: bool)
        =
        match ManagedCopyRegistrationAttestation.parse current.Registration, signer with
        | Some original, Some(publicKey, publicDigest, true, CopySignerPurpose.CopyAttestor) ->
            current.State = "DELETE_PENDING"
            && current.Revision + 1L = transition.Revision
            && current.EventHash = transition.PreviousEventHash
            && ManagedCopyTransitionPolicy.sameCopy original transition.Copy
            && transition.Copy.InstallationId = witness.Identity.InstallationId
            && transition.Copy.LineageId = witness.Identity.LineageId
            && transition.Copy.Epoch = witness.Identity.Epoch
            && transition.ActionWitnessCutoffSequence >= original.WitnessCutoffSequence
            && current.RetainUntil <= now
            && not held
            && publicDigest = SHA256.HashData(publicKey)
        | _ -> false

    let private absenceMatches
        (transition: ManagedCopyTransition)
        (absence: VerifiedManagedCopyDeletionAbsence)
        =
        absence.CopyId = transition.Copy.CopyId
        && absence.WitnessCutoffSequence = transition.ActionWitnessCutoffSequence
        && absence.WitnessCutoffHash = transition.ActionWitnessCutoffHash
        && transition.DeletionProofSha256 = Some absence.InspectionReportSha256
        && transition.LastVerifiedAt = Some absence.ObservedAt
        && absence.VerifierSigningKeyId <> transition.Copy.SigningKeyId
        && absence.RegistryHolderActorId <> absence.VerifierHolderActorId

    let private commit
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (transition: ManagedCopyTransition)
        canonical
        signature
        absence
        =
        task {
            let exact = ManagedCopyTransitionPolicy.candidate transition canonical signature

            try
                let intent =
                    witness.BeginAuthority(
                        transition.Copy.EventId,
                        exact,
                        transition.Copy.SourceCaseId
                    )

                do!
                    ManagedCopyVerifiedDeletionWrite.apply
                        connection
                        transaction
                        transition
                        canonical
                        signature
                        absence
                        intent

                do! transaction.CommitAsync()
                witness.SettleAuthority(transition.Copy.EventId, intent) |> ignore
                return AuthorityWriteOutcome.Applied(transition.Copy.EventId, transition.Revision)
            finally
                CryptographicOperations.ZeroMemory(exact)
        }

    let private completeEvidence
        connection
        transaction
        witness
        (transition: ManagedCopyTransition)
        canonical
        signature
        (state: ManagedCopyCurrent)
        now
        (proof: VerifiedManagedCopyDeletionAbsence)
        =
        task {
            let! copyHolder = holder connection transaction transition.Copy.SigningKeyId

            match copyHolder with
            | Some holderId when holderId <> proof.VerifierHolderActorId ->
                let! approvalExpiry =
                    ManagedCopyDeletionEvidenceRead.verify
                        connection
                        transaction
                        witness
                        transition
                        proof
                        state.Revision
                        transition.Copy.LocationCommitment
                        holderId
                        now

                match approvalExpiry with
                | None -> return AuthorityWriteOutcome.Refused
                | Some expires ->
                    let! fresh = Sql.databaseNow connection transaction

                    if
                        fresh >= expires
                        || fresh >= proof.RegistryExpiresAt
                        || fresh >= proof.InspectionExpiresAt
                    then
                        return AuthorityWriteOutcome.Refused
                    else
                        return!
                            commit
                                connection
                                transaction
                                witness
                                transition
                                canonical
                                signature
                                proof
            | _ -> return AuthorityWriteOutcome.Refused
        }

    let private verifyAbsence
        connection
        transaction
        (witness: WitnessProtocol)
        (verifier: ICopyAbsenceVerifier)
        (transition: ManagedCopyTransition)
        canonical
        signature
        state
        now
        =
        task {
            let historical =
                try
                    witness.VerifyHistoricalTip(
                        transition.ActionWitnessCutoffSequence,
                        transition.ActionWitnessCutoffHash
                    )

                    true
                with _ ->
                    false

            if not historical then
                return AuthorityWriteOutcome.Refused
            else
                let! absence =
                    verifier.Verify(
                        connection,
                        transaction,
                        witness,
                        transition.Copy.CopyId,
                        transition.ActionWitnessCutoffSequence,
                        transition.ActionWitnessCutoffHash,
                        now,
                        CancellationToken.None
                    )

                match absence with
                | Some proof when absenceMatches transition proof ->
                    return!
                        completeEvidence
                            connection
                            transaction
                            witness
                            transition
                            canonical
                            signature
                            state
                            now
                            proof
                | _ -> return AuthorityWriteOutcome.Refused
        }

    let newEvent
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (verifier: ICopyAbsenceVerifier)
        (transition: ManagedCopyTransition)
        canonical
        signature
        =
        task {
            let! current =
                ManagedCopyOwnerRead.current connection transaction transition.Copy.CopyId

            let! signer =
                ManagedCopyOwnerRead.signer connection transaction transition.Copy.SigningKeyId

            let! now = Sql.databaseNow connection transaction

            let! held =
                ManagedCopyTransitionAdministration.held
                    connection
                    transaction
                    transition.Copy.SourceCaseId

            match current, signer with
            | Some state, Some(publicKey, _, true, CopySignerPurpose.CopyAttestor) when
                exactCopy witness transition state signer now held
                && ManagedCopySignature.verify publicKey canonical signature
                ->
                return!
                    verifyAbsence
                        connection
                        transaction
                        witness
                        verifier
                        transition
                        canonical
                        signature
                        state
                        now
            | _ -> return AuthorityWriteOutcome.Refused
        }
