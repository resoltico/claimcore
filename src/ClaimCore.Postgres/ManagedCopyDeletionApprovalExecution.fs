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
        =
        match prior with
        | Some(bytes, digest, sequence, epoch, entryHash) when
            bytes = canonical && digest = SHA256.HashData(canonical)
            ->
            witness.VerifyAuthorityEvidence(request.ApprovalId, sequence, epoch, entryHash, digest)
            CopyDeletionApprovalOutcome.Approved(request.ApprovalId, revision)
        | _ -> CopyDeletionApprovalOutcome.ResourceUnavailable

    let private fresh
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (request: CopyDeletionApprovalRequest)
        actorId
        revision
        canonical
        =
        task {
            let! copy =
                ManagedCopyDeletionApprovalTarget.read connection transaction witness request

            match copy with
            | None -> return CopyDeletionApprovalOutcome.ResourceUnavailable
            | Some value ->
                let! held = activeHold connection transaction value.SourceCaseId
                let! time = Sql.databaseNow connection transaction

                let historical =
                    try
                        witness.VerifyHistoricalTip(
                            request.WitnessCutoffSequence,
                            request.WitnessCutoffHash
                        )

                        true
                    with _ ->
                        false

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
        }

    let private persistOrReplay
        connection
        transaction
        witness
        (request: CopyDeletionApprovalRequest)
        actorId
        revision
        canonical
        =
        task {
            let! prior = existing connection transaction request.ApprovalId

            match prior with
            | Some _ -> return replay witness request canonical prior revision
            | None ->
                return! fresh connection transaction witness request actorId revision canonical
        }

    let private underLock
        connection
        transaction
        (witness: WitnessProtocol)
        context
        (request: CopyDeletionApprovalRequest)
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
                finally
                    CryptographicOperations.ZeroMemory(canonical)
            | _ -> return CopyDeletionApprovalOutcome.ResourceUnavailable
        }

    let approve
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (request: CopyDeletionApprovalRequest)
        =
        task {
            if not (validRequest context request) then
                return CopyDeletionApprovalOutcome.ResourceUnavailable
            else
                try
                    witness.Admit()
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared
                            (Some dataSource)
                            connection
                            System.Threading.CancellationToken.None

                    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
                    return! underLock connection transaction witness context request
                with _ ->
                    return CopyDeletionApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }
