namespace ClaimCore.Application.Reads

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open Npgsql

module internal NoteReads =
    let findNote (tx: ReadTx) id =
        let connection = tx.Connection
        let token = tx.CancellationToken
        NoteStorage.find connection id token

    let notePage (tx: ReadTx) (query: NoteStorage.PageQuery) =
        let connection = tx.Connection
        let token = tx.CancellationToken
        NoteStorage.page connection query token
