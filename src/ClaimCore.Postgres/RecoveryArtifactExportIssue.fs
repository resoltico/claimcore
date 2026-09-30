namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.RecordFormat
open WitnessProtocolReconciliation

module internal RecoveryArtifactExportIssue =
    let private persistedInstant (value: DateTimeOffset) =
        let utc = value.ToUniversalTime()
        DateTimeOffset(utc.Ticks - utc.Ticks % 10L, TimeSpan.Zero)

    let private existing
        connection
        transaction
        (witness: WitnessProtocol)
        operationId
        caseId
        exporter
        revision
        now
        (row: RecoveryArtifactExportRow)
        (validateExisting: byte array -> bool)
        ct
        =
        task {
            let! managed =
                RecoveryArtifactExportRead.managedCopyMatches connection transaction row ct

            if
                row.OperationId <> operationId
                || row.CaseId <> caseId
                || row.ExporterActorId <> exporter
                || row.ExporterGrantRevision <> revision
                || row.ExpiresAt <= now
                || row.WitnessEpoch <> witness.Identity.Epoch
                || not managed
                || not (validateExisting row.ArtifactBytes)
                || not (
                    CryptographicOperations.FixedTimeEquals(
                        ReadOnlySpan<byte>(row.ArtifactSha256),
                        ReadOnlySpan<byte>(SHA256.HashData(row.ArtifactBytes))
                    )
                )
            then
                return Error CoreFault.RecoveryMutationUnknown
            else
                try
                    RecoveryArtifactExportRead.reconcileSettled witness row
                    return Ok row.ArtifactBytes
                with _ ->
                    return Error CoreFault.RecoveryMutationUnknown
        }

    let private admitted
        connection
        transaction
        (context: ActorCallContext)
        (retained: RetainedPreparation)
        ct
        =
        task {
            let! revision = ActorGrantRead.lockRevision connection transaction true ct

            do!
                Sql.lockKeyAsync
                    connection
                    transaction
                    ("operation:" + retained.OperationId.ToString("D"))

            let! available =
                RecoveryArtifactExportAdmission.activeCase connection transaction retained ct

            let! exact =
                RecoveryArtifactExportAdmission.retainedExact connection transaction retained ct

            let! unrevoked =
                RecoveryArtifactExportAdmission.notRevoked
                    connection
                    transaction
                    retained.OperationId
                    ct

            let! authorized =
                ActorMutationGuard.authorizeScope
                    connection
                    transaction
                    context
                    (ResourceScope.Operation(retained.OperationId, retained.CaseId))
                    revision

            return
                context.Action = EndpointAction.RecoveryExport
                && context.CaseId = Some retained.CaseId
                && available
                && exact
                && unrevoked
                && authorized
        }

    let private fresh
        connection
        transaction
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (retained: RetainedPreparation)
        (key: RecoveryArtifactIssueKey)
        encrypt
        exportId
        instant
        progress
        ct
        =
        task {
            let! used =
                RecoveryArtifactExportAdmission.countForKey connection transaction key.Id ct

            if used >= key.MaximumExports then
                return Error CoreFault.RecoveryCapacityExhausted
            else
                let! bytes =
                    RecoveryArtifactExportCommit.commit
                        connection
                        transaction
                        witness
                        context
                        retained
                        key
                        exportId
                        instant
                        used
                        encrypt
                        progress
                        ct

                return Ok bytes
        }

    let private issueUnderLock
        connection
        transaction
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (retained: RetainedPreparation)
        (key: RecoveryArtifactIssueKey)
        (now: unit -> DateTimeOffset)
        (encrypt: RecoveryArtifactV3 -> byte array)
        (validateExisting: byte array -> bool)
        exportId
        progress
        ct
        =
        task {
            let instant = now () |> persistedInstant

            match! RecoveryArtifactExportRead.find connection (Some transaction) exportId ct with
            | Some row ->
                return!
                    existing
                        connection
                        transaction
                        witness
                        retained.OperationId
                        retained.CaseId
                        context.Binding.ActorId
                        context.Binding.GrantRevision
                        instant
                        row
                        validateExisting
                        ct
            | None when instant < key.IssueFrom || instant >= key.IssueUntil ->
                return Error CoreFault.RecoveryStoreUnavailable
            | None ->
                return!
                    fresh
                        connection
                        transaction
                        witness
                        context
                        retained
                        key
                        encrypt
                        exportId
                        instant
                        progress
                        ct
        }

    let private eventId
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (retained: RetainedPreparation)
        =
        RecoveryArtifactExportCandidate.exportId
            witness.Identity.InstallationId
            witness.Identity.Epoch
            retained.OperationId
            context.Binding.ActorId
            context.Binding.GrantRevision

    let issue
        (dataSource: NpgsqlDataSource)
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (retained: RetainedPreparation)
        (key: RecoveryArtifactIssueKey)
        (now: unit -> DateTimeOffset)
        (encrypt: RecoveryArtifactV3 -> byte array)
        (validateExisting: byte array -> bool)
        (ct: CancellationToken)
        : Task<Result<byte array, CoreFault>> =
        task {
            let exportId = eventId witness context retained

            let mutable commitStarted = false

            try
                ct.ThrowIfCancellationRequested()
                use! connection = RuntimeDatabase.openConnectionAsyncWithCancellation dataSource ct

                use! _authorityLease =
                    AuthorityOperationFence.acquireShared (Some dataSource) connection ct

                use! transaction =
                    connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

                let! allowed = admitted connection transaction context retained ct

                if not allowed then
                    return Error CoreFault.RecoveryStoreUnavailable
                else
                    let progress = ref false

                    try
                        return!
                            issueUnderLock
                                connection
                                transaction
                                witness
                                context
                                retained
                                key
                                now
                                encrypt
                                validateExisting
                                exportId
                                progress
                                ct
                    finally
                        commitStarted <- progress.Value
            with
            | :? OperationCanceledException when not commitStarted ->
                return Error CoreFault.RecoveryMutationCancelled
            | :? WitnessPending -> return Error CoreFault.RecoveryMutationUnknown
            | _ when commitStarted -> return Error CoreFault.RecoveryMutationUnknown
            | _ -> return Error CoreFault.RecoveryStoreUnavailable
        }
