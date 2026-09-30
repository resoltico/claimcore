namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

/// Owner-private candidate derivation only; no approval, witness append or release occurs.
module internal WriterHandoffOwnerAbortDraft =
    let private fullPendingAudit dataSource (witness: WitnessProtocol) commitments cutoff =
        task {
            use! audit = RuntimeDatabase.openConnectionAsync dataSource

            let! summary =
                DataAudit.runWithSuppression audit witness commitments CancellationToken.None

            return summary.PendingIntents = 1L && summary.WitnessCutoff = cutoff
        }

    let private signers
        connection
        transaction
        (witness: WitnessProtocol)
        firstKey
        secondKey
        cutoff
        =
        let first = WriterHandoffAbortApproval.current connection transaction firstKey
        let second = WriterHandoffAbortApproval.current connection transaction secondKey

        if
            firstKey = secondKey
            || first.Holder = second.Holder
            || first.RegisteredSequence >= cutoff
            || second.RegisteredSequence >= cutoff
            || first.RetiredSequence.IsSome
            || second.RetiredSequence.IsSome
        then
            invalidOp "Two current abort owners are unavailable."

        for signer in [ first; second ] do
            witness.VerifyAuthorityEvidenceForInstallation(
                signer.RegisteredEventId,
                signer.RegisteredSequence,
                signer.RegisteredEpoch,
                signer.RegisteredHash,
                signer.RegisteredCandidate
            )

        first, second

    let private value
        (witness: WitnessProtocol)
        (prepared: PrimaryWriterPreparation)
        authorityRevision
        (now: DateTimeOffset)
        (oldCapability: byte array)
        (first: WriterHandoffAbortSigner)
        (second: WriterHandoffAbortSigner)
        =
        let validUntil =
            DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(10.).ToUnixTimeSeconds())

        {
            HandoffId = prepared.Value.HandoffId
            InstallationId = witness.Identity.InstallationId
            LineageId = witness.Identity.LineageId
            Epoch = witness.Identity.Epoch
            OldGeneration = prepared.Value.OldGeneration
            PrepareSequence = prepared.Intent.Sequence
            PrepareHash = prepared.Intent.EntryHash
            PrepareCanonicalSha256 = SHA256.HashData(prepared.Canonical)
            OldCapabilitySha256 = SHA256.HashData(oldCapability)
            NewCapabilitySha256 = prepared.Value.NewCapabilitySha256
            AbortSigningKeyOneId = first.KeyId
            AbortSigningKeyTwoId = second.KeyId
            ApprovalOneId = Guid.NewGuid()
            ApprovalTwoId = Guid.NewGuid()
            OwnerOneActorId = first.Holder
            OwnerTwoActorId = second.Holder
            OwnerOneGrantRevision = first.GrantRevision
            OwnerTwoGrantRevision = second.GrantRevision
            ExpectedAuthorityRevision = authorityRevision
            ValidUntil = validUntil
        }

    let private exactPending
        (witness: WitnessProtocol)
        handoffId
        (prepared: PrimaryWriterPreparation)
        (snapshot: Snapshot)
        primaryGeneration
        firstKey
        secondKey
        =
        handoffId <> Guid.Empty
        && firstKey <> secondKey
        && snapshot.HandoffPending
        && snapshot.TipSequence = prepared.Intent.Sequence
        && snapshot.TipHash = prepared.Intent.EntryHash
        && snapshot.WriterGeneration = prepared.Value.OldGeneration
        && primaryGeneration = prepared.Value.OldGeneration
        && (witness.EvidenceStore.TryReadHandoff(handoffId)
            |> Option.forall (fun entry -> entry.AbortSequence.IsNone))

    let private candidate
        primaryOwner
        transaction
        dataSource
        witness
        commitments
        firstKey
        secondKey
        (prepared: PrimaryWriterPreparation)
        revision
        now
        oldCapability
        =
        task {
            let first, second =
                signers primaryOwner transaction witness firstKey secondKey prepared.Intent.Sequence

            let! audited =
                fullPendingAudit dataSource witness commitments prepared.Intent.Sequence

            if not audited then
                return None
            else
                let canonical =
                    value witness prepared revision now oldCapability first second
                    |> WriterHandoffAbort.encode

                return
                    if WriterHandoffAbort.parse canonical |> Option.isSome then
                        Some canonical
                    else
                        None
        }

    let private state primaryOwner transaction (witness: WitnessProtocol) handoffId =
        task {
            let! revision =
                ActorGrantRead.lockRevision primaryOwner transaction true CancellationToken.None

            let! now = ManagedCopySignerPolicy.databaseNow primaryOwner transaction

            let prepared =
                WriterHandoffOwnerRead.preparation primaryOwner transaction witness handoffId
                |> Option.defaultWith (fun () -> invalidOp "Primary handoff is absent.")

            let snapshot = witness.Snapshot()

            let primaryGeneration, _, _, _ =
                WriterHandoffOwnerReconcile.primaryState primaryOwner transaction

            return revision, now, prepared, snapshot, primaryGeneration
        }

    let private deriveUnderLock
        (primaryOwner: NpgsqlConnection)
        dataSource
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments option)
        handoffId
        firstKey
        secondKey
        (oldCapability: byte array)
        =
        task {
            use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision, now, prepared, snapshot, primaryGeneration =
                state primaryOwner transaction witness handoffId

            if
                not (
                    exactPending
                        witness
                        handoffId
                        prepared
                        snapshot
                        primaryGeneration
                        firstKey
                        secondKey
                )
            then
                return None
            else
                return!
                    candidate
                        primaryOwner
                        transaction
                        dataSource
                        witness
                        commitments
                        firstKey
                        secondKey
                        prepared
                        revision
                        now
                        oldCapability
        }

    let create
        (primaryOwner: NpgsqlConnection)
        dataSource
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments option)
        handoffId
        firstKey
        secondKey
        (oldCapability: byte array)
        =
        task {
            try
                OwnerConnection.requireIdentity primaryOwner
                SchemaBaseline.requireCurrent primaryOwner
                witness.AdmitReadOnly()

                use! _authorityFence =
                    AuthorityOperationFence.acquireExclusive
                        None
                        primaryOwner
                        CancellationToken.None

                return!
                    deriveUnderLock
                        primaryOwner
                        dataSource
                        witness
                        commitments
                        handoffId
                        firstKey
                        secondKey
                        oldCapability
            with _ ->
                return None
        }
