namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open WitnessProtocolReconciliation
open ManagedCopyDeletionApprovalPolicy

/// Actor-bound witnessed approval execution; uncertain output is never called non-commit.
module internal ManagedCopyDeletionApproval =
    let private replay
        (witness: WitnessProtocol)
        (request: CopyDeletionApprovalRequest)
        (canonical: byte array)
        (prior: (byte array * byte array * int64 * int64 * byte array) option)
        (revision: int64)
        ct
        =
        task {
            match prior with
            | Some(bytes, digest, sequence, epoch, entryHash) when
                bytes = canonical && digest = SHA256.HashData(canonical)
                ->
                do!
                    witness.VerifyAuthorityEvidence(
                        request.ApprovalId,
                        sequence,
                        epoch,
                        entryHash,
                        digest,
                        ct
                    )

                return CopyDeletionApprovalOutcome.Approved(request.ApprovalId, revision)
            | _ -> return CopyDeletionApprovalOutcome.ResourceUnavailable
        }

    let private fresh
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (request: CopyDeletionApprovalRequest)
        actorId
        revision
        canonical
        ct
        =
        task {
            let! copy =
                ManagedCopyDeletionApprovalTarget.read connection transaction witness request

            match copy with
            | None -> return CopyDeletionApprovalOutcome.ResourceUnavailable
            | Some value ->
                let! held = activeHold connection transaction value.SourceCaseId
                let! time = Sql.databaseNow connection transaction ct

                let! historical =
                    task {
                        try
                            do!
                                witness.VerifyHistoricalTip(
                                    request.WitnessCutoffSequence,
                                    request.WitnessCutoffHash,
                                    ct
                                )

                            return true
                        with _ ->
                            return false
                    }

                if not historical || not (admissible request value actorId time held) then
                    return CopyDeletionApprovalOutcome.ResourceUnavailable
                else
                    return!
                        apply
                            connection
                            transaction
                            witness
                            request
                            actorId
                            revision
                            value
                            canonical
                            ct
        }

    let private persistOrReplay
        connection
        transaction
        witness
        (request: CopyDeletionApprovalRequest)
        actorId
        revision
        canonical
        ct
        =
        task {
            let! prior = existing connection transaction request.ApprovalId

            match prior with
            | Some _ -> return! replay witness request canonical prior revision ct
            | None ->
                return! fresh connection transaction witness request actorId revision canonical ct
        }

    let private underLock
        connection
        transaction
        (witness: WitnessProtocol)
        context
        (request: CopyDeletionApprovalRequest)
        ct
        =
        task {
            let! revision =
                ActorGrantRead.lockRevision connection transaction true CancellationToken.None

            let! authority =
                ActorGrantRead.loadUnderLock
                    connection
                    transaction
                    context.Binding.Principal
                    ResourceScope.Installation
                    revision
                    CancellationToken.None

            match authority with
            | Some live when authorized context live revision ->
                let canonical =
                    ManagedCopyDeletionApprovalCandidate.canonical request live.ActorId revision

                try
                    return!
                        persistOrReplay
                            connection
                            transaction
                            witness
                            request
                            live.ActorId
                            revision
                            canonical
                            ct
                finally
                    CryptographicOperations.ZeroMemory(canonical)
            | _ -> return CopyDeletionApprovalOutcome.ResourceUnavailable
        }

    let approve
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (request: CopyDeletionApprovalRequest)
        ct
        =
        task {
            if not (validRequest context request) then
                return CopyDeletionApprovalOutcome.ResourceUnavailable
            else
                try
                    do! witness.Admit(ct)

                    use! connection =
                        RuntimeDatabase.openConnectionAsyncWithCancellation dataSource ct

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared (Some dataSource) connection ct

                    use! transaction =
                        connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

                    return! underLock connection transaction witness context request ct
                with _ ->
                    return CopyDeletionApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }
