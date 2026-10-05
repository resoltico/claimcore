namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
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
        task {
            let! observed =
                InstallationLossRetirementState.verifiedOwnerAuthority
                    primaryOwner
                    transaction
                    witness
                    cutoff

            if observed <> revision then
                invalidOp "Loss owner authority projection diverged."

            return!
                InstallationLossRetirementSigners.pair
                    primaryOwner
                    transaction
                    witness
                    firstKey
                    secondKey
                    CancellationToken.None
        }

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
        task {
            let! first, second =
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
                return None
            else
                return Some canonical
        }

    let private currentDecisionState primaryOwner transaction (witness: WitnessProtocol) =
        task {
            let identity, retired =
                InstallationLossRetirementState.primaryIdentity primaryOwner transaction

            let revision =
                InstallationLossRetirementState.authorityRevision primaryOwner transaction

            let! now = InstallationLossRetirementState.databaseNow primaryOwner transaction
            let! snapshot = witness.Snapshot(CancellationToken.None)

            if
                retired
                || snapshot.LossRetirementPending
                || snapshot.LossRetired
                || not (InstallationLossRetirementState.matchingIdentity identity witness)
            then
                return None
            else
                return Some(identity, snapshot, revision, now)
        }

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
        task {
            try
                OwnerConnection.requireIdentity primaryOwner
                SchemaBaseline.requireCurrent primaryOwner
                do! witness.AdmitReadOnly(CancellationToken.None)

                use! _authorityFence =
                    AuthorityOperationFence.acquireShared None primaryOwner CancellationToken.None

                use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

                let! current = currentDecisionState primaryOwner transaction witness

                match current with
                | None -> return None
                | Some(identity, snapshot, revision, now) ->
                    return!
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
                return None
        }
