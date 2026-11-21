namespace ClaimCore.Application.Reads

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open Npgsql

module internal LedgerReads =
    let timelinePage (tx: ReadTx) (query: TimelineStorage.PageQuery) =
        let connection = tx.Connection
        let cipher = tx.Cipher
        let token = tx.CancellationToken
        TimelineStorage.page connection cipher query token

    let previousEntries (tx: ReadTx) entries =
        let connection = tx.Connection
        let cipher = tx.Cipher
        let token = tx.CancellationToken
        TimelineStorage.previous connection cipher entries token

    let findOperation (tx: ReadTx) id =
        let connection = tx.Connection
        let cipher = tx.Cipher
        let token = tx.CancellationToken
        StoredEntries.operation connection cipher id token
