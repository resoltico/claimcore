namespace ClaimCore.Postgres

open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

/// Owner-only terminal incident interface; it never promotes a degraded writer.
module internal InstallationLossRetirementAdministration =
    let draft
        (primaryOwner: NpgsqlConnection)
        (witness: WitnessProtocol)
        (suppression: ISuppressionCommitments)
        firstKey
        secondKey
        evidenceReport
        checkpoint
        knownOperations
        mode
        =
        InstallationLossRetirementDraft.create
            primaryOwner
            witness
            suppression
            firstKey
            secondKey
            evidenceReport
            checkpoint
            knownOperations
            mode

    let record
        (primaryOwner: NpgsqlConnection)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (suppression: ISuppressionCommitments)
        canonical
        signatureOne
        signatureTwo
        knownOperations
        evidenceReport
        checkpoint
        =
        InstallationLossRetirementCommit.record
            primaryOwner
            ownerWitnessConnection
            witness
            suppression
            canonical
            signatureOne
            signatureTwo
            knownOperations
            evidenceReport
            checkpoint

    /// Reconciliation can only drive the same signed candidate and source bytes forward.
    let reconcile
        primaryOwner
        ownerWitnessConnection
        witness
        suppression
        canonical
        signatureOne
        signatureTwo
        knownOperations
        evidenceReport
        checkpoint
        =
        record
            primaryOwner
            ownerWitnessConnection
            witness
            suppression
            canonical
            signatureOne
            signatureTwo
            knownOperations
            evidenceReport
            checkpoint
