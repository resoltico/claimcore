namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal LossRetirementCommitInput =
    {
        Decision: InstallationLossRetirementDecision
        DecisionBytes: byte array
        OwnerSignatureOne: byte array
        OwnerSignatureTwo: byte array
        OperationIdentitySource: byte array
        ReportEvidence: byte array option
        CheckpointEvidence: byte array option
    }

/// Recheck current independent actor authority, owner keys and every signed input
/// before W0 can acquire an irreversible witness fence.
module internal InstallationLossRetirementPreflight =
    let private snapshotMatches
        (value: InstallationLossRetirementDecision)
        (snapshot: Snapshot)
        retired
        (now: DateTimeOffset)
        =
        (not retired
         && not snapshot.LossRetirementPending
         && not snapshot.LossRetired
         && value.ValidUntil > now
         && value.ValidUntil <= now.AddMinutes(10.)
         && snapshot.TipSequence = value.PreviousSequence
         && snapshot.TipHash = value.PreviousHash)
        || ((snapshot.LossRetirementPending || snapshot.LossRetired)
            && snapshot.LossRetirementId = Some value.RetirementId)

    let private matches
        identity
        (witness: WitnessProtocol)
        revision
        count
        digest
        first
        second
        (input: LossRetirementCommitInput)
        snapshot
        retired
        now
        =
        let value = input.Decision

        InstallationLossRetirementState.matchingIdentity identity witness
        && value.InstallationId = identity.InstallationId
        && value.LineageId = identity.LineageId
        && value.Epoch = identity.Epoch
        && value.AuthorityRevision = revision
        && value.KnownOperationCount = count
        && value.KnownOperationDigest = digest
        && InstallationLossRetirementState.expectedEvidence
            value
            input.ReportEvidence
            input.CheckpointEvidence
        && InstallationLossRetirementSigners.verify
            first
            second
            value
            input.DecisionBytes
            input.OwnerSignatureOne
            input.OwnerSignatureTwo
        && snapshotMatches value snapshot retired now

    let private authority primaryOwner transaction (witness: WitnessProtocol) =
        let identity, retired =
            InstallationLossRetirementState.primaryIdentity primaryOwner transaction

        let revision =
            InstallationLossRetirementState.authorityRevision primaryOwner transaction

        let now = InstallationLossRetirementState.databaseNow primaryOwner transaction
        let snapshot = witness.Snapshot()

        if
            InstallationLossRetirementState.verifiedOwnerAuthority
                primaryOwner
                transaction
                witness
                snapshot.TipSequence
            <> revision
        then
            invalidOp "Loss owner authority projection diverged."

        identity, retired, revision, now, snapshot

    let check
        (primaryOwner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (suppression: ISuppressionCommitments)
        (input: LossRetirementCommitInput)
        =
        let value = input.Decision

        let identity, retired, revision, now, snapshot =
            authority primaryOwner transaction witness

        let count, digest, commitments =
            InstallationLossOperationCommitments.commitments
                suppression
                value.OperationSet
                input.OperationIdentitySource

        let first, second =
            InstallationLossRetirementSigners.pair
                primaryOwner
                transaction
                witness
                value.SignerOneId
                value.SignerTwoId

        if
            not (
                matches
                    identity
                    witness
                    revision
                    count
                    digest
                    first
                    second
                    input
                    snapshot
                    retired
                    now
            )
        then
            invalidOp "Loss retirement evidence diverged."

        commitments, retired
