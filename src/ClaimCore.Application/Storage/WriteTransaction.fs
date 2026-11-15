namespace ClaimCore.Application.Storage

open System
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// The store constructs writes only after lock_ledger_head returns in IStore.Write.
type internal WriteTx(connection, installation, head: LedgerPosition, observedAt, token) =
    inherit ReadTx(connection, installation, observedAt, token)

    member _.Head = head

    member _.AdvanceExpiryFrontier candidate =
        ExpiryFrontierStorage.advance connection candidate token

    member _.NewPayloadNonce() =
        System.Security.Cryptography.RandomNumberGenerator.GetBytes 32

    member _.Append(entry: RetainedOperation) =
        task {
            FailureDiagnostics.phase FailurePhase.Append
            let started = System.Diagnostics.Stopwatch.GetTimestamp()

            try
                let! _ = OperationStorage.append connection entry.Root token
                return ()
            finally
                ApplicationMetrics.appended (System.Diagnostics.Stopwatch.GetElapsedTime started)
        }

    member _.ProjectCase projection =
        FailureDiagnostics.phase FailurePhase.Projection
        Persistence.case connection projection token

    member _.ProjectCaseIndex(id, index) =
        FailureDiagnostics.phase FailurePhase.Projection
        Persistence.caseIndex connection id index token

    member _.ProjectWorkChange projection =
        FailureDiagnostics.phase FailurePhase.Projection
        Persistence.workChange connection projection token

    member _.DismissAccessRequest principal =
        task {
            let! _ = AccessRequests.lockPrincipal connection principal token
            let! _ = AccessRequests.delete connection principal token
            return ()
        }

    member _.ProjectPeople projection =
        FailureDiagnostics.phase FailurePhase.Projection
        Persistence.people connection projection token
