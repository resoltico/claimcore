namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

/// Owner-only irreversible witness payload operation. It never changes ERASURE_PENDING into a
/// final erasure claim: retained metadata, backup/WAL/export copies and suppression stay auditable.
module internal CaseTombstonePruneOwner =
    let private pendingReceipt ownerConnection caseId =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync()
            use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)

            let! receipt = CaseTombstonePrunePrimaryRead.find connection transaction caseId

            do! transaction.CommitAsync()
            return receipt.IsSome
        }

    let private preflightAudit ownerConnection witness commitments ct =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)

            let! _ = DataAudit.runWithSuppression connection witness (Some commitments) ct

            return ()
        }

    let private resume
        witnessOwnerConnection
        connection
        transaction
        witness
        inventory
        revision
        stored
        (proposal: TombstonePruneProposal)
        instant
        ct
        =
        task {
            let! receipt =
                CaseTombstonePrunePrimaryRead.find connection transaction proposal.CaseId

            match receipt with
            | Some accepted ->
                try
                    return!
                        CaseTombstonePruneOwnerCommit.retry
                            witnessOwnerConnection
                            connection
                            transaction
                            witness
                            revision
                            proposal
                            accepted
                            instant
                with _ ->
                    return OwnerWitnessPruneOutcome.Unconfirmed proposal.EventId
            | None ->
                try
                    return!
                        CaseTombstonePruneOwnerCommit.initial
                            witnessOwnerConnection
                            connection
                            transaction
                            witness
                            inventory
                            revision
                            stored
                            proposal
                            instant
                            ct
                with _ ->
                    return OwnerWitnessPruneOutcome.Unconfirmed proposal.EventId
        }

    let private transact
        ownerConnection
        witnessOwnerConnection
        (witness: WitnessProtocol)
        (inventory: IManagedCopyErasureClearance)
        (proposal: TombstonePruneProposal)
        (ct: CancellationToken)
        =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)
            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision = ActorGrantRead.lockRevision connection transaction true ct

            let! found = CaseTombstoneRead.lock connection transaction proposal.CaseId

            match found with
            | None -> return OwnerWitnessPruneOutcome.ResourceUnavailable
            | Some stored ->
                let! instant = Sql.databaseNow connection transaction

                return!
                    resume
                        witnessOwnerConnection
                        connection
                        transaction
                        witness
                        inventory
                        revision
                        stored
                        proposal
                        instant
                        ct
        }

    let private readyForPrune ownerConnection witness commitments caseId ct =
        task {
            let! pending = pendingReceipt ownerConnection caseId

            if pending then
                return true
            else
                try
                    do! preflightAudit ownerConnection witness commitments ct
                    return true
                with _ ->
                    return false
        }

    let execute
        ownerConnection
        witnessOwnerConnection
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (inventory: IManagedCopyErasureClearance)
        (proposal: TombstonePruneProposal)
        (ct: CancellationToken)
        =
        task {
            if not (CaseTombstonePruneOwnerChecks.validProposal proposal) then
                return OwnerWitnessPruneOutcome.Refused LifecycleRefusal.InvalidIdentity
            else
                try
                    witness.Admit()
                    commitments.Admit()
                    use fenceConnection = new NpgsqlConnection(ownerConnection)
                    do! fenceConnection.OpenAsync(ct)
                    OwnerConnection.requireIdentity fenceConnection
                    SchemaBaseline.requireCurrent fenceConnection

                    use! _authorityFence =
                        AuthorityOperationFence.acquireExclusive None fenceConnection ct

                    let! audited =
                        readyForPrune ownerConnection witness commitments proposal.CaseId ct

                    if not audited then
                        return OwnerWitnessPruneOutcome.AuditUnavailable "preprune-full-audit"
                    else
                        return!
                            transact
                                ownerConnection
                                witnessOwnerConnection
                                witness
                                inventory
                                proposal
                                ct
                with
                | :? OperationCanceledException when ct.IsCancellationRequested ->
                    return OwnerWitnessPruneOutcome.Unconfirmed proposal.EventId
                | :? InvalidDataException ->
                    return OwnerWitnessPruneOutcome.AuditUnavailable "prune-evidence"
                | _ -> return OwnerWitnessPruneOutcome.AuditUnavailable "owner-admission"
        }
