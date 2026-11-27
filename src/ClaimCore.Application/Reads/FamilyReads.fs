namespace ClaimCore.Application.Reads

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open Npgsql

module internal FamilyReads =
    let findCase (tx: ReadTx) id =
        let connection = tx.Connection
        let installation = tx.InstallationId
        let token = tx.CancellationToken
        Projections.caseById connection installation id token

    let caseFamilySequence (tx: ReadTx) id =
        let connection = tx.Connection
        let token = tx.CancellationToken

        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT coalesce(max(operation_sequence),0) FROM claimcore.ledger_effects WHERE aggregate_kind='FAMILY' AND aggregate_id=$1"
                    [| box (CaseId.value id) |]
                    token

            return
                DatabaseSession.value<int64> 0 rows.Head
                |> DatabaseSession.admitted Sequence.create
        }

    let findCaseByReference (tx: ReadTx) reference =
        task {
            let! id = CaseIdentityReads.resolve tx (CaseSelector.Reference reference)

            match id with
            | Some id -> return! findCase tx id
            | None -> return None
        }

    let caseStageFacts (tx: ReadTx) caseId =
        let connection = tx.Connection
        let cipher = tx.Cipher
        let token = tx.CancellationToken
        TimelineStorage.stages connection cipher caseId token

    let casePage (tx: ReadTx) (query: TransactionPages.CaseQuery) =
        let connection = tx.Connection
        let installation = tx.InstallationId
        let token = tx.CancellationToken
        TransactionPages.cases connection installation query token

    let caseExists (tx: ReadTx) selection =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    ($"SELECT EXISTS(SELECT 1 FROM claimcore.cases c WHERE {CaseSelectionSql.predicate selection})")
                    (CaseSelectionSql.parameters selection)
                    tx.CancellationToken

            return DatabaseSession.value<bool> 0 rows.Head
        }

    let caseSummary (tx: ReadTx) query groupBy after limit cacheKey =
        let connection = tx.Connection
        let token = tx.CancellationToken
        let cacheKey = if tx.ExportSnapshot then None else cacheKey
        CaseSummaryQuery.read connection query groupBy after limit cacheKey tx.ObservedAt token
