namespace ClaimCore.Postgres

open System.Security.Cryptography

open System
open System.Threading
open Npgsql
open ClaimCore.Witness

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal InstallationUseActivationOutcome =
    | Activated of eventId: Guid * witnessSequence: int64 * witnessHash: byte array
    | Refused
    | Unconfirmed of eventId: Guid

/// Witness-first one-way release, exact primary projection, and paired readback.
module internal InstallationUseActivationCommit =
    let private pairedOutcome primary witness eventId sequence hash ct =
        task {
            let! paired = InstallationUseScopeRead.requirePair primary witness ct

            if
                paired.Scope = InstallationUseScope.RealData
                && paired.Phase = InstallationUsePhase.Active
                && paired.ActivationEventId = Some eventId
                && paired.ActivationSequence = Some sequence
                && paired.ActivationHash = Some hash
            then
                return InstallationUseActivationOutcome.Activated(eventId, sequence, hash)
            else
                return InstallationUseActivationOutcome.Unconfirmed eventId
        }

    let settleAndPersist
        primary
        transaction
        ownerWitness
        witness
        proof
        plan
        approvals
        eventId
        canonical
        (started: bool ref)
        ct
        =
        task {
            started.Value <- true

            let! intent, settled =
                InstallationUseActivationWitness.activate
                    ownerWitness
                    witness
                    eventId
                    proof.WitnessTipSequence
                    proof.WitnessTipHash
                    canonical
                    ct

            do!
                InstallationUseActivationPrimary.accept
                    primary
                    transaction
                    proof
                    plan
                    approvals
                    eventId
                    canonical
                    intent
                    settled
                    CancellationToken.None

            do! transaction.CommitAsync(CancellationToken.None)

            return!
                pairedOutcome
                    primary
                    witness
                    eventId
                    (fst settled)
                    (snd settled)
                    CancellationToken.None
        }

    let commitApprovalPair
        primaryOwner
        transaction
        ownerWitnessConnection
        witness
        proof
        plan
        pair
        eventId
        started
        ct
        =
        task {
            let canonical = InstallationUseActivationCandidate.encodeFinal plan proof pair

            try
                return!
                    settleAndPersist
                        primaryOwner
                        transaction
                        ownerWitnessConnection
                        witness
                        proof
                        plan
                        pair
                        eventId
                        canonical
                        started
                        ct
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }
