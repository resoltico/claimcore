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

    let caseState (tx: ReadTx) id =
        let connection = tx.Connection
        let installation = tx.InstallationId
        let token = tx.CancellationToken

        task {
            let! record = Projections.caseById connection installation id token

            match record with
            | None -> return None
            | Some record ->
                let! payments = PaymentStorage.owned connection id token

                return
                    CaseSubject.restore
                        record.Index.ChangedAt
                        record.Snapshot
                        (payments |> List.map (fun row -> row.PaymentId, row) |> Map.ofList)
                    |> Result.map Some
                    |> Result.defaultWith (fun _ ->
                        raise (
                            RecordCorruptException
                                "Case payment state does not match its aggregate."
                        ))
        }

    let caseFamilySequence (tx: ReadTx) id =
        let connection = tx.Connection
        let token = tx.CancellationToken

        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT coalesce(max(operation_sequence),0) FROM claimcore.ledger_effects WHERE (subject_kind='CASE' AND subject_id=$1) OR parent_case_id=$1"
                    [| box (CaseId.value id) |]
                    token

            return
                DatabaseSession.value<int64> 0 rows.Head
                |> DatabaseSession.admitted Sequence.create
        }

    let caseFamily (tx: ReadTx) id =
        let connection = tx.Connection
        let token = tx.CancellationToken
        CaseFamilyStorage.load connection id token

    let findCaseByReference (tx: ReadTx) reference =
        let connection = tx.Connection
        let installation = tx.InstallationId
        let token = tx.CancellationToken
        Projections.caseByReference connection installation reference token

    let caseStageFacts (tx: ReadTx) caseId =
        let connection = tx.Connection
        let installation = tx.InstallationId
        let token = tx.CancellationToken
        TimelineStorage.stages connection installation caseId token

    let casePage (tx: ReadTx) (query: TransactionPages.CaseQuery) =
        let connection = tx.Connection
        let installation = tx.InstallationId
        let token = tx.CancellationToken
        TransactionPages.cases connection installation query token

    let caseSummary (tx: ReadTx) query =
        let connection = tx.Connection
        let token = tx.CancellationToken
        CaseSummaryQuery.read connection query token
