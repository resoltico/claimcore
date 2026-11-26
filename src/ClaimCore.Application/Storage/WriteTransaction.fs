namespace ClaimCore.Application.Storage

open System
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// The store constructs writes only after lock_ledger_head returns in IStore.Write.
type internal WriteTx
    (connection, installation, cipher, head: LedgerPosition, observedAt, caseReferenceEntropy, token)
    =
    inherit ReadTx(connection, installation, cipher, observedAt, token)

    member _.Head = head

    member _.DrawCaseReference() =
        CaseReference.generate (caseReferenceEntropy ())

    member _.AdvanceExpiryFrontier candidate =
        ExpiryFrontierStorage.advance connection candidate token

    member _.NewPayloadNonce() =
        System.Security.Cryptography.RandomNumberGenerator.GetBytes 32

    member private _.AppendCore(entry: RetainedOperation, attachment: VerifiedAttachment option) =
        task {
            FailureDiagnostics.phase FailurePhase.Append
            let started = System.Diagnostics.Stopwatch.GetTimestamp()

            try
                let! _ =
                    match attachment with
                    | Some payload ->
                        OperationStorage.appendWithAttachment connection cipher entry payload token
                    | None -> OperationStorage.append connection cipher entry.Root token

                return ()
            finally
                ApplicationMetrics.appended (System.Diagnostics.Stopwatch.GetElapsedTime started)
        }

    member this.Append(entry: RetainedOperation) = this.AppendCore(entry, None)

    member this.Append(entry: RetainedOperation, attachment: VerifiedAttachment) =
        this.AppendCore(entry, Some attachment)

    member _.ProjectFamily projection =
        FailureDiagnostics.phase FailurePhase.Projection
        PartProjections.family connection projection token

    member _.ProjectPerson projection =
        FailureDiagnostics.phase FailurePhase.Projection
        PartProjections.person connection projection token
