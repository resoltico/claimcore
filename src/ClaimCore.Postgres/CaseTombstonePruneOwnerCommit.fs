namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open CaseTombstonePruneOwnerChecks

module internal CaseTombstonePruneOwnerCommit =
    let private settle
        witnessOwnerConnection
        (witness: WitnessProtocol)
        (proposal: TombstonePruneProposal)
        (seal: WitnessPruneSeal)
        (intent: WitnessIntent)
        (approvals: PruneApprovalReceipt list)
        ct
        =
        task {
            try
                let! result =
                    CaseWitnessPayloadPrune.settle
                        witnessOwnerConnection
                        witness
                        proposal.CaseId
                        proposal.PurgeEventId
                        proposal.PurgeWitnessSequence
                        (digest proposal.PurgeWitnessHash)
                        seal
                        intent
                        approvals
                        ct

                return
                    OwnerWitnessPruneOutcome.WitnessPayloadPruned(
                        proposal.EventId,
                        result.DeletedCount
                    )
            with _ ->
                return OwnerWitnessPruneOutcome.Unconfirmed proposal.EventId
        }

    let private commitAccepted
        ownerWitnessConnection
        connection
        transaction
        (witness: WitnessProtocol)
        (proposal: TombstonePruneProposal)
        (seal: WitnessPruneSeal)
        (approvals: PruneApprovalReceipt list)
        (copyDigest: byte array)
        ct
        =
        task {
            let canonical =
                CaseTombstonePruneExecutionCandidate.encode proposal copyDigest approvals

            try
                try
                    let! intent =
                        witness.BeginAuthority(
                            proposal.EventId,
                            canonical,
                            Some proposal.CaseId,
                            ct
                        )

                    do!
                        CaseTombstonePrunePrimaryWrite.persist
                            connection
                            transaction
                            witness
                            proposal
                            copyDigest
                            canonical
                            intent
                            ct

                    do! transaction.CommitAsync(ct)

                    return!
                        settle
                            ownerWitnessConnection
                            witness
                            proposal
                            seal
                            intent
                            approvals
                            CancellationToken.None
                with _ ->
                    return OwnerWitnessPruneOutcome.Unconfirmed proposal.EventId
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let private commitWithInventory
        witnessOwnerConnection
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (inventory: IManagedCopyErasureClearance)
        (proposal: TombstonePruneProposal)
        (seal: WitnessPruneSeal)
        (approvals: PruneApprovalReceipt list)
        (ct: CancellationToken)
        =
        task {
            let! copy =
                inventory.RequireCompleteInventory(
                    connection,
                    transaction,
                    witness,
                    proposal.CaseId,
                    seal.CutoffSequence,
                    seal.CutoffHash,
                    ct
                )

            match copy with
            | None -> return OwnerWitnessPruneOutcome.InventoryUnknown
            | Some evidence when not (matchesCopySeal proposal evidence) ->
                return OwnerWitnessPruneOutcome.InventoryUnknown
            | Some evidence ->
                return!
                    commitAccepted
                        witnessOwnerConnection
                        connection
                        transaction
                        witness
                        proposal
                        seal
                        approvals
                        evidence.InventorySha256
                        ct
        }

    let initial
        witnessOwnerConnection
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (inventory: IManagedCopyErasureClearance)
        authorityRevision
        (stored: StoredCaseTombstone)
        (proposal: TombstonePruneProposal)
        (observedAt: DateTimeOffset)
        (ct: CancellationToken)
        =
        task {
            if not (matchesTombstone proposal stored) then
                return OwnerWitnessPruneOutcome.Refused LifecycleRefusal.VersionConflict
            elif stored.PruneEventId.IsSome || proposal.ValidUntil <= observedAt then
                return OwnerWitnessPruneOutcome.Refused LifecycleRefusal.ApprovalExpired
            else
                let! preparation =
                    preflight
                        connection
                        transaction
                        witness
                        authorityRevision
                        stored
                        proposal
                        observedAt
                        ct

                match preparation with
                | Error refusal -> return refusal
                | Ok(approvals, seal) ->
                    return!
                        commitWithInventory
                            witnessOwnerConnection
                            connection
                            transaction
                            witness
                            inventory
                            proposal
                            seal
                            approvals
                            ct
        }

    let private historicalApprovals connection transaction witness revision proposal observedAt ct =
        CaseTombstonePruneOwnerApprovals.read
            connection
            transaction
            witness
            revision
            proposal
            false
            observedAt
            ct

    let private acceptedIntent
        witness
        (proposal: TombstonePruneProposal)
        (stored: StoredWitnessPruneReceipt)
        canonical
        ct
        =
        CaseWitnessPayloadPrune.readExactIntent
            witness
            proposal.CaseId
            proposal.EventId
            stored.IntentSequence
            stored.IntentHash
            canonical
            ct

    let retry
        witnessOwnerConnection
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        authorityRevision
        (proposal: TombstonePruneProposal)
        (stored: StoredWitnessPruneReceipt)
        (observedAt: DateTimeOffset)
        ct
        =
        task {
            if not (matchesStoredFields proposal stored) then
                return OwnerWitnessPruneOutcome.Refused LifecycleRefusal.ApprovalMismatch
            else
                let! approvals =
                    historicalApprovals
                        connection
                        transaction
                        witness
                        authorityRevision
                        proposal
                        observedAt
                        ct

                let canonical =
                    CaseTombstonePruneExecutionCandidate.encode
                        proposal
                        stored.CopyInventoryDigest
                        approvals

                try
                    if not (matchesStored proposal canonical stored) then
                        return OwnerWitnessPruneOutcome.Refused LifecycleRefusal.ApprovalMismatch
                    else
                        let! intent = acceptedIntent witness proposal stored canonical ct

                        do! transaction.CommitAsync()

                        return!
                            settle
                                witnessOwnerConnection
                                witness
                                proposal
                                (targetSeal proposal)
                                intent
                                approvals
                                CancellationToken.None
                finally
                    CryptographicOperations.ZeroMemory(canonical)
        }
