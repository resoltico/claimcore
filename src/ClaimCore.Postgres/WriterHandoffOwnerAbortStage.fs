namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal WriterHandoffAbortOutcome =
    | AwaitingPrimary of handoffId: Guid * sequence: int64 * hash: byte array
    | AwaitingRelease of handoffId: Guid * sequence: int64 * hash: byte array
    | Released of handoffId: Guid * sequence: int64 * hash: byte array
    | Refused
    | Unconfirmed of handoffId: Guid

/// A1: current owners sign the exact abort, and witness retains its pending fence.
module internal WriterHandoffOwnerAbortStage =
    let private matches
        (witness: WitnessProtocol)
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffAbort)
        (snapshot: Snapshot)
        authorityRevision
        (oldCapability: byte array)
        now
        =
        value.InstallationId = witness.Identity.InstallationId
        && value.LineageId = witness.Identity.LineageId
        && value.Epoch = witness.Identity.Epoch
        && value.HandoffId = prepared.Value.HandoffId
        && value.OldGeneration = prepared.Value.OldGeneration
        && value.PrepareSequence = prepared.Intent.Sequence
        && value.PrepareHash = prepared.Intent.EntryHash
        && value.PrepareCanonicalSha256 = SHA256.HashData(prepared.Canonical)
        && value.NewCapabilitySha256 = prepared.Value.NewCapabilitySha256
        && value.OldCapabilitySha256 = SHA256.HashData(oldCapability)
        && value.ExpectedAuthorityRevision = authorityRevision
        && value.ValidUntil > now
        && value.ValidUntil <= now.AddMinutes(10.)
        && snapshot.HandoffPending
        && snapshot.WriterGeneration = value.OldGeneration
        && snapshot.TipSequence = value.PrepareSequence
        && snapshot.TipHash = value.PrepareHash

    let private fullPendingAudit dataSource (witness: WitnessProtocol) commitments expected =
        task {
            use! audit = RuntimeDatabase.openConnectionAsync dataSource

            let! summary =
                DataAudit.runWithSuppression audit witness commitments CancellationToken.None

            return summary.PendingIntents = 1L && summary.WitnessCutoff = expected
        }

    let private appendAbort
        ownerWitnessConnection
        witness
        value
        canonical
        signatureOne
        signatureTwo
        oldCapability
        =
        try
            let ticket =
                WriterHandoffWitnessAbortCommands.abort
                    ownerWitnessConnection
                    witness
                    value
                    canonical
                    signatureOne
                    signatureTwo
                    oldCapability

            WriterHandoffAbortOutcome.AwaitingPrimary(
                value.HandoffId,
                ticket.Sequence,
                ticket.EntryHash
            )
        with _ ->
            WriterHandoffAbortOutcome.Unconfirmed value.HandoffId

    let private fresh
        (primaryOwner: NpgsqlConnection)
        transaction
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        commitments
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        oldCapability
        revision
        now
        =
        task {
            let snapshot = witness.Snapshot()

            let primaryGeneration, _, _, _ =
                WriterHandoffOwnerReconcile.primaryState primaryOwner transaction

            if
                not (matches witness prepared value snapshot revision oldCapability now)
                || primaryGeneration <> value.OldGeneration
            then
                return WriterHandoffAbortOutcome.Refused
            else
                WriterHandoffAbortApproval.verifyCurrent
                    primaryOwner
                    transaction
                    witness
                    value
                    canonical
                    signatureOne
                    signatureTwo
                |> ignore

                let! audited = fullPendingAudit dataSource witness commitments value.PrepareSequence

                if not audited then
                    return WriterHandoffAbortOutcome.Refused
                else
                    return
                        appendAbort
                            ownerWitnessConnection
                            witness
                            value
                            canonical
                            signatureOne
                            signatureTwo
                            oldCapability
        }

    let private existingAbort witness prepared value canonical signatureOne signatureTwo =
        WriterHandoffOwnerAbortEvidence.read
            witness
            prepared
            value
            canonical
            signatureOne
            signatureTwo

    let private reconcileOrAppend
        primaryOwner
        transaction
        dataSource
        ownerWitnessConnection
        witness
        commitments
        prepared
        value
        canonical
        signatureOne
        signatureTwo
        oldCapability
        revision
        now
        =
        task {
            match existingAbort witness prepared value canonical signatureOne signatureTwo with
            | Some ticket ->
                return
                    WriterHandoffAbortOutcome.AwaitingPrimary(
                        value.HandoffId,
                        ticket.Sequence,
                        ticket.EntryHash
                    )
            | None ->
                return!
                    fresh
                        primaryOwner
                        transaction
                        dataSource
                        ownerWitnessConnection
                        witness
                        commitments
                        prepared
                        value
                        canonical
                        signatureOne
                        signatureTwo
                        oldCapability
                        revision
                        now
        }

    let private startParsed
        (primaryOwner: NpgsqlConnection)
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments option)
        canonical
        (signatureOne: byte array)
        (signatureTwo: byte array)
        (oldCapability: byte array)
        (value: WriterHandoffAbort)
        =
        task {
            OwnerConnection.requireIdentity primaryOwner
            SchemaBaseline.requireCurrent primaryOwner
            witness.AdmitReadOnly()

            use! _authorityFence =
                AuthorityOperationFence.acquireExclusive None primaryOwner CancellationToken.None

            use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision =
                ActorGrantRead.lockRevision primaryOwner transaction true CancellationToken.None

            let! now = ManagedCopySignerPolicy.databaseNow primaryOwner transaction

            let prepared =
                WriterHandoffOwnerRead.preparation primaryOwner transaction witness value.HandoffId
                |> Option.defaultWith (fun () -> invalidOp "Primary handoff is absent.")

            return!
                reconcileOrAppend
                    primaryOwner
                    transaction
                    dataSource
                    ownerWitnessConnection
                    witness
                    commitments
                    prepared
                    value
                    canonical
                    signatureOne
                    signatureTwo
                    oldCapability
                    revision
                    now
        }

    let start
        primaryOwner
        dataSource
        ownerWitnessConnection
        witness
        commitments
        canonical
        signatureOne
        signatureTwo
        oldCapability
        =
        task {
            match WriterHandoffAbort.parse canonical with
            | None -> return WriterHandoffAbortOutcome.Refused
            | Some value ->
                try
                    return!
                        startParsed
                            primaryOwner
                            dataSource
                            ownerWitnessConnection
                            witness
                            commitments
                            canonical
                            signatureOne
                            signatureTwo
                            oldCapability
                            value
                with _ ->
                    return WriterHandoffAbortOutcome.Refused
        }
