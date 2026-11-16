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

    member _.ProjectFamily projection =
        FailureDiagnostics.phase FailurePhase.Projection
        PartProjections.family connection projection token

    member _.ProjectPerson projection =
        FailureDiagnostics.phase FailurePhase.Projection
        PartProjections.person connection projection token
