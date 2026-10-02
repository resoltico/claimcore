namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open WitnessProtocolReconciliation

/// Owner-only VERIFY→RETAINED for one physically inspected encrypted BASE/WAL object.
/// It does not establish a complete two-cluster recovery horizon or cutover readiness.
module internal ManagedCopyVerifiedRestore =
    let private exactRetry
        (connection: NpgsqlConnection)
        transaction
        (witness: WitnessProtocol)
        (transition: ManagedCopyTransition)
        canonical
        signature
        stored
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT report_sha256 FROM claimcore.managed_copy_verifications "
                    + "WHERE verification_event_id=@event AND copy_id=@copy "
                    + "AND copy_revision=@revision",
                    connection,
                    transaction
                )

            Sql.uuid command "event" transition.Copy.EventId
            Sql.uuid command "copy" transition.Copy.CopyId
            Sql.integer command "revision" transition.Revision
            let! proof = command.ExecuteScalarAsync()

            return
                match proof with
                | :? (byte array) as digest when
                    transition.Copy.VerificationProofSha256 = Some digest
                    ->
                    ManagedCopyTransitionAdministration.exactRetry
                        witness
                        transition
                        canonical
                        signature
                        stored
                | _ -> AuthorityWriteOutcome.Unconfirmed transition.Copy.EventId
        }

    let private signedTransition
        (witness: WitnessProtocol)
        (transition: ManagedCopyTransition)
        (state: ManagedCopyCurrent)
        (publicKey: byte array)
        (publicDigest: byte array)
        canonical
        signature
        =
        match ManagedCopyRegistrationAttestation.parse state.Registration with
        | Some original when
            transition.EventKind = "VERIFY"
            && transition.State = "RETAINED"
            && original.Kind = transition.Copy.Kind
            && (original.Kind = "BASE" || original.Kind = "WAL")
            && ManagedCopyTransitionPolicy.sameCopy original transition.Copy
            && publicDigest = SHA256.HashData(publicKey)
            && ManagedCopySignature.verify publicKey canonical signature
            && transition.ActionWitnessCutoffSequence >= original.WitnessCutoffSequence
            && transition.Copy.InstallationId = witness.Identity.InstallationId
            && transition.Copy.LineageId = witness.Identity.LineageId
            && transition.Copy.Epoch = witness.Identity.Epoch
            ->
            Some original
        | _ -> None

    let private commit
        (connection: NpgsqlConnection)
        transaction
        (witness: WitnessProtocol)
        (transition: ManagedCopyTransition)
        canonical
        signature
        (verified: VerifiedManagedCopyRestore)
        =
        task {
            let candidate = ManagedCopyTransitionPolicy.candidate transition canonical signature

            try
                let intent = witness.BeginAuthority(transition.Copy.EventId, candidate, None)

                let eventHash =
                    ManagedCopyEventHash.compute
                        transition.PreviousEventHash
                        canonical
                        (Some signature)

                do!
                    ManagedCopyVerifiedRestoreWrite.apply
                        connection
                        transaction
                        transition
                        canonical
                        signature
                        verified
                        eventHash
                        intent

                do! transaction.CommitAsync()
                witness.SettleAuthority(transition.Copy.EventId, intent) |> ignore
                return AuthorityWriteOutcome.Applied(transition.Copy.EventId, transition.Revision)
            finally
                CryptographicOperations.ZeroMemory(candidate)
        }

    let private proofMatches
        connection
        transaction
        witness
        transition
        state
        (original: ManagedCopyAttestation)
        (proof: VerifiedManagedCopyRestore)
        now
        =
        ManagedCopyPhysicalVerificationPolicy.matches witness transition state original proof now
        && ManagedCopyPhysicalVerificationPolicy.signer
            connection
            transaction
            proof.Proof
            proof.Canonical
            proof.Signature
            original.SigningKeyId

    let private verifiedEvent
        (connection: NpgsqlConnection)
        transaction
        (witness: WitnessProtocol)
        (verifier: IManagedCopyPhysicalVerifier)
        (transition: ManagedCopyTransition)
        canonical
        signature
        (state: ManagedCopyCurrent)
        (original: ManagedCopyAttestation)
        now
        =
        task {
            witness.VerifyHistoricalTip(
                transition.ActionWitnessCutoffSequence,
                transition.ActionWitnessCutoffHash
            )

            let! physical =
                verifier.Verify(
                    connection,
                    transaction,
                    witness,
                    transition,
                    state,
                    now,
                    CancellationToken.None
                )

            match physical with
            | Some proof when
                proofMatches connection transaction witness transition state original proof now
                ->
                let! fresh = Sql.databaseNow connection transaction

                if proof.Proof.ValidUntil <= fresh || state.RetainUntil <= fresh then
                    return AuthorityWriteOutcome.Refused
                else
                    return!
                        commit connection transaction witness transition canonical signature proof
            | _ -> return AuthorityWriteOutcome.Refused
        }

    let private newEvent
        (connection: NpgsqlConnection)
        transaction
        (witness: WitnessProtocol)
        (verifier: IManagedCopyPhysicalVerifier)
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

            match current, signer with
            | Some state, Some(publicKey, publicDigest, true, CopySignerPurpose.CopyAttestor) ->
                match
                    signedTransition
                        witness
                        transition
                        state
                        publicKey
                        publicDigest
                        canonical
                        signature
                with
                | None -> return AuthorityWriteOutcome.Refused
                | Some original ->
                    return!
                        verifiedEvent
                            connection
                            transaction
                            witness
                            verifier
                            transition
                            canonical
                            signature
                            state
                            original
                            now
            | _ -> return AuthorityWriteOutcome.Refused
        }

    let private underLock
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        (verifier: IManagedCopyPhysicalVerifier)
        (value: ManagedCopyTransition)
        canonical
        signature
        =
        task {
            use! _authorityFence =
                AuthorityOperationFence.acquireShared None connection CancellationToken.None

            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

            let! _ =
                ActorGrantRead.lockRevision connection transaction true CancellationToken.None

            let! existing =
                ManagedCopyOwnerRead.transitionEvent connection transaction value.Copy.EventId

            match existing with
            | Some stored ->
                return! exactRetry connection transaction witness value canonical signature stored
            | None ->
                return! newEvent connection transaction witness verifier value canonical signature
        }

    let execute
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        (verifier: IManagedCopyPhysicalVerifier)
        (canonical: byte array)
        (signature: byte array)
        =
        task {
            match ManagedCopyTransitionAttestation.parse canonical with
            | None -> return AuthorityWriteOutcome.Refused
            | Some value when
                value.EventKind <> "VERIFY"
                || value.State <> "RETAINED"
                || isNull (box signature)
                || signature.Length <> 64
                ->
                return AuthorityWriteOutcome.Refused
            | Some value ->
                try
                    OwnerConnection.requireIdentity connection
                    SchemaBaseline.requireCurrent connection
                    witness.Admit()
                    return! underLock connection witness verifier value canonical signature
                with _ ->
                    return AuthorityWriteOutcome.Unconfirmed value.Copy.EventId
        }
