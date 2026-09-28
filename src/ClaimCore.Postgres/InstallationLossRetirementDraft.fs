namespace ClaimCore.Postgres

open System
open System.Data
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

/// Read-only, owner-private derivation of the exact incident decision bytes.
module internal InstallationLossRetirementDraft =
    let private value
        identity
        (snapshot: Snapshot)
        revision
        (now: DateTimeOffset)
        mode
        count
        digest
        evidenceReport
        checkpoint
        (first: InstallationLossSigner)
        (second: InstallationLossSigner)
        =
        {
            RetirementId = Guid.NewGuid()
            InstallationId = identity.InstallationId
            LineageId = identity.LineageId
            Epoch = identity.Epoch
            PreviousSequence = snapshot.TipSequence
            PreviousHash = snapshot.TipHash
            EvidenceReportSha256 = InstallationLossRetirementCandidate.evidenceDigest evidenceReport
            IndependentCheckpointSha256 =
                InstallationLossRetirementCandidate.evidenceDigest checkpoint
            OperationSet = mode
            KnownOperationCount = count
            KnownOperationDigest = digest
            SignerOneId = first.KeyId
            SignerTwoId = second.KeyId
            OwnerOneActorId = first.Holder
            OwnerTwoActorId = second.Holder
            OwnerOneGrantRevision = first.GrantRevision
            OwnerTwoGrantRevision = second.GrantRevision
            AuthorityRevision = revision
            ValidUntil = DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(10.).ToUnixTimeSeconds())
        }

    let private verifiedSigners
        primaryOwner
        transaction
        witness
        cutoff
        revision
        firstKey
        secondKey
        =
        if
            InstallationLossRetirementState.verifiedOwnerAuthority
                primaryOwner
                transaction
                witness
                cutoff
            <> revision
        then
            invalidOp "Loss owner authority projection diverged."

        InstallationLossRetirementSigners.pair primaryOwner transaction witness firstKey secondKey

    let private candidate
        primaryOwner
        transaction
        witness
        suppression
        firstKey
        secondKey
        evidenceReport
        checkpoint
        knownOperations
        mode
        identity
        snapshot
        revision
        now
        =
        let first, second =
            verifiedSigners
                primaryOwner
                transaction
                witness
                snapshot.TipSequence
                revision
                firstKey
                secondKey

        let count, digest, _ =
            InstallationLossOperationCommitments.commitments suppression mode knownOperations

        let canonical =
            value
                identity
                snapshot
                revision
                now
                mode
                count
                digest
                evidenceReport
                checkpoint
                first
                second
            |> InstallationLossRetirementCandidate.encode

        if InstallationLossRetirementCandidate.parse canonical |> Option.isNone then
            None
        else
            Some canonical

    let create
        (primaryOwner: NpgsqlConnection)
        (witness: WitnessProtocol)
        (suppression: ISuppressionCommitments)
        firstKey
        secondKey
        (evidenceReport: byte array option)
        (checkpoint: byte array option)
        knownOperations
        mode
        =
        try
            OwnerConnection.requireIdentity primaryOwner
            SchemaBaseline.requireCurrent primaryOwner
            witness.AdmitReadOnly()
            use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

            let identity, retired =
                InstallationLossRetirementState.primaryIdentity primaryOwner transaction

            let revision =
                InstallationLossRetirementState.authorityRevision primaryOwner transaction

            let now = InstallationLossRetirementState.databaseNow primaryOwner transaction
            let snapshot = witness.Snapshot()

            if
                retired
                || snapshot.LossRetirementPending
                || snapshot.LossRetired
                || not (InstallationLossRetirementState.matchingIdentity identity witness)
            then
                None
            else
                candidate
                    primaryOwner
                    transaction
                    witness
                    suppression
                    firstKey
                    secondKey
                    evidenceReport
                    checkpoint
                    knownOperations
                    mode
                    identity
                    snapshot
                    revision
                    now
        with _ ->
            None
