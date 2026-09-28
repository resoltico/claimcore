namespace ClaimCore.Postgres

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
    let private pairedOutcome primary witness eventId sequence hash =
        let paired = InstallationUseScopeRead.requirePair primary witness

        if
            paired.Scope = InstallationUseScope.RealData
            && paired.Phase = InstallationUsePhase.Active
            && paired.ActivationEventId = Some eventId
            && paired.ActivationSequence = Some sequence
            && paired.ActivationHash = Some hash
        then
            InstallationUseActivationOutcome.Activated(eventId, sequence, hash)
        else
            InstallationUseActivationOutcome.Unconfirmed eventId

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
        =
        task {
            started.Value <- true

            let intent, settled =
                InstallationUseActivationWitness.activate
                    ownerWitness
                    witness
                    eventId
                    proof.WitnessTipSequence
                    proof.WitnessTipHash
                    canonical

            InstallationUseActivationPrimary.insert
                primary
                transaction
                proof
                plan
                approvals
                eventId
                canonical
                intent
                settled

            InstallationUseActivationApprovals.consume primary transaction eventId approvals
            InstallationUseActivationPrimary.release primary transaction eventId settled
            do! transaction.CommitAsync(CancellationToken.None)
            return pairedOutcome primary witness eventId (fst settled) (snd settled)
        }
