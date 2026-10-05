namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open WitnessProtocolReconciliation

/// Owner-only witnessed adopted-copy changes, bound to the independently verified adoption.
module internal ManagedCopyAdoptedTransitionAdministration =
    let private exactRetry
        (witness: WitnessProtocol)
        (value: AdoptedCopyTransition)
        (canonical: byte array)
        (signature: byte array)
        (stored: AdoptedCopyStoredEvent)
        =
        task {
            let candidate =
                ManagedCopyAdoptedTransitionPolicy.candidate value canonical signature

            try
                if
                    stored.CopyId <> value.CopyId
                    || stored.AdoptionEventId <> value.AdoptionEventId
                    || stored.Revision <> value.Revision
                    || stored.EventKind <> value.EventKind
                    || stored.ProducerKind <> value.ProducerKind
                    || stored.SigningKeyId <> stored.CustodianKeyId
                    || stored.Canonical <> canonical
                    || stored.Signature <> signature
                    || stored.PreviousHash <> value.PreviousEventHash
                    || stored.EventHash
                       <> ManagedCopyEventHash.compute
                           value.PreviousEventHash
                           canonical
                           (Some signature)
                    || stored.CandidateSha256 <> SHA256.HashData(candidate)
                then
                    return AuthorityWriteOutcome.Refused
                else
                    do!
                        witness.VerifyAuthorityEvidenceForCase(
                            value.EventId,
                            stored.WitnessSequence,
                            stored.WitnessEpoch,
                            stored.WitnessEntryHash,
                            stored.CandidateSha256,
                            value.SourceCaseId,
                            CancellationToken.None
                        )

                    return AuthorityWriteOutcome.Applied(value.EventId, value.Revision)
            finally
                CryptographicOperations.ZeroMemory(candidate)
        }

    let private deletionReady
        connection
        transaction
        (witness: WitnessProtocol)
        (verifier: ICopyAbsenceVerifier option)
        (value: AdoptedCopyTransition)
        (origin: VerifiedCopyAdoptionOrigin)
        (state: AdoptedCopyCurrent)
        now
        =
        match value.EventKind, verifier with
        | "VERIFIED_DELETED", Some proofVerifier ->
            ManagedCopyAdoptedDeletionEvidence.verify
                connection
                transaction
                witness
                proofVerifier
                value
                origin
                state
                now
        | "VERIFIED_DELETED", None -> task { return false }
        | _, None -> task { return true }
        | _, Some _ -> task { return false }

    let private applyWithOrigin
        connection
        transaction
        (witness: WitnessProtocol)
        (verifier: ICopyAbsenceVerifier option)
        (value: AdoptedCopyTransition)
        (origin: VerifiedCopyAdoptionOrigin)
        (state: AdoptedCopyCurrent)
        canonical
        signature
        =
        task {
            let! eligible =
                ManagedCopyAdoptedTransitionChecks.eligible
                    connection
                    transaction
                    witness
                    value
                    origin
                    state
                    canonical
                    signature

            match eligible with
            | None -> return AuthorityWriteOutcome.Refused
            | Some now ->
                let! ready =
                    deletionReady connection transaction witness verifier value origin state now

                if ready then
                    return!
                        ManagedCopyAdoptedTransitionWrite.commit
                            connection
                            transaction
                            witness
                            value
                            origin.CustodianSigningKeyId
                            canonical
                            signature
                else
                    return AuthorityWriteOutcome.Refused
        }

    let private newEvent
        connection
        transaction
        (witness: WitnessProtocol)
        (verifier: ICopyAbsenceVerifier option)
        (value: AdoptedCopyTransition)
        canonical
        signature
        =
        task {
            let! state = ManagedCopyAdoptedTransitionRead.current connection transaction value

            let! origin =
                ManagedCopyAdoptionEvidence.verifyOrigin
                    connection
                    transaction
                    witness
                    value.ActionWitnessCutoffSequence
                    value.CopyId
                    CancellationToken.None

            match state, origin with
            | Some state, Some origin when
                ManagedCopyAdoptedTransitionPolicy.identity origin value
                && value.Revision = state.Revision + 1L
                && value.PreviousEventHash = state.EventHash
                ->
                return!
                    applyWithOrigin
                        connection
                        transaction
                        witness
                        verifier
                        value
                        origin
                        state
                        canonical
                        signature
            | _ -> return AuthorityWriteOutcome.Refused
        }

    let private underLock
        connection
        transaction
        (witness: WitnessProtocol)
        (verifier: ICopyAbsenceVerifier option)
        (value: AdoptedCopyTransition)
        canonical
        signature
        =
        task {
            let! prior =
                ManagedCopyAdoptedTransitionRead.existing connection transaction value.EventId

            match prior with
            | Some stored when value.EventKind = "VERIFIED_DELETED" ->
                let! consumed =
                    ManagedCopyVerifiedDeletion.used
                        connection
                        transaction
                        (value.DeletionApprovalId |> Option.get)
                        value.EventId

                if consumed then
                    return! exactRetry witness value canonical signature stored
                else
                    return AuthorityWriteOutcome.Unconfirmed value.EventId
            | Some stored -> return! exactRetry witness value canonical signature stored
            | None ->
                return! newEvent connection transaction witness verifier value canonical signature
        }

    let private execute
        connection
        (witness: WitnessProtocol)
        (verifier: ICopyAbsenceVerifier option)
        (canonical: byte array)
        (signature: byte array)
        =
        task {
            match ManagedCopyAdoptedTransitionAttestation.parse canonical with
            | None -> return AuthorityWriteOutcome.Refused
            | Some _ when isNull (box signature) || signature.Length <> 64 ->
                return AuthorityWriteOutcome.Refused
            | Some value ->
                try
                    OwnerConnection.requireIdentity connection
                    SchemaBaseline.requireCurrent connection
                    do! witness.Admit(CancellationToken.None)

                    use! _authorityFence =
                        AuthorityOperationFence.acquireShared None connection CancellationToken.None

                    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

                    let! _ =
                        ActorGrantRead.lockRevision
                            connection
                            transaction
                            true
                            CancellationToken.None

                    return!
                        underLock connection transaction witness verifier value canonical signature
                with _ ->
                    return AuthorityWriteOutcome.Unconfirmed value.EventId
        }

    let transition connection witness canonical signature =
        task {
            match ManagedCopyAdoptedTransitionAttestation.parse canonical with
            | Some value when value.EventKind <> "VERIFIED_DELETED" ->
                return! execute connection witness None canonical signature
            | _ -> return AuthorityWriteOutcome.Refused
        }

    let verifiedDeletion connection witness (verifier: ICopyAbsenceVerifier) canonical signature =
        task {
            match ManagedCopyAdoptedTransitionAttestation.parse canonical with
            | Some value when value.EventKind = "VERIFIED_DELETED" ->
                return! execute connection witness (Some verifier) canonical signature
            | _ -> return AuthorityWriteOutcome.Refused
        }
