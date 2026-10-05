namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open WitnessProtocolReconciliation

/// Rechecks custodian authority, signed bytes, retention and holds under owner locks.
module internal ManagedCopyAdoptedTransitionChecks =
    let eligible
        connection
        transaction
        (witness: WitnessProtocol)
        (value: AdoptedCopyTransition)
        (origin: VerifiedCopyAdoptionOrigin)
        (state: AdoptedCopyCurrent)
        canonical
        signature
        =
        task {
            let! signer =
                ManagedCopyOwnerRead.signer connection transaction origin.CustodianSigningKeyId

            let! now = Sql.databaseNow connection transaction CancellationToken.None

            let! held =
                ManagedCopyTransitionAdministration.held connection transaction (Some origin.CaseId)

            let! historical =
                task {
                    try
                        do!
                            witness.VerifyHistoricalTip(
                                value.ActionWitnessCutoffSequence,
                                value.ActionWitnessCutoffHash,
                                CancellationToken.None
                            )

                        return true
                    with _ ->
                        return false
                }

            match signer with
            | Some(publicKey, digest, true, CopySignerPurpose.CopyAttestor) when
                historical
                && digest = SHA256.HashData(publicKey)
                && ManagedCopySignature.verify publicKey canonical signature
                && ManagedCopyAdoptedTransitionPolicy.allowed
                    state.State
                    state.VerificationProofSha256
                    state.LastVerifiedAt
                    state.RetainUntil
                    value
                    now
                    held
                ->
                return Some now
            | _ -> return None
        }
