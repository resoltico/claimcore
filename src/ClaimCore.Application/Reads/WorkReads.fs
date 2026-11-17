namespace ClaimCore.Application.Reads

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open Npgsql

module internal WorkReads =
    let findWork (tx: ReadTx) id =
        let connection = tx.Connection
        let token = tx.CancellationToken
        WorkStorage.findWork connection id token

    let workSummaries (tx: ReadTx) (ids, today) =
        let connection = tx.Connection
        let token = tx.CancellationToken
        WorkStorage.summaries connection ids today token

    let assignedAccounts (tx: ReadTx) pairs =
        let connection = tx.Connection
        let token = tx.CancellationToken
        WorkStorage.accounts connection pairs token

    let responsibilityCounts (tx: ReadTx) (selection, actors) =
        let connection = tx.Connection
        let token = tx.CancellationToken
        ResponsibilityCountStorage.counts connection selection actors token

    let caseViewCounts (tx: ReadTx) selection =
        let connection = tx.Connection
        let token = tx.CancellationToken
        ViewCountStorage.cases connection selection token

    let reminderViewCounts (tx: ReadTx) selection =
        let connection = tx.Connection
        let token = tx.CancellationToken
        ViewCountStorage.reminders connection selection token
