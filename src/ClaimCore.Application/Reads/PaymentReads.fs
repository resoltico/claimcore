namespace ClaimCore.Application.Reads

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open Npgsql

module internal PaymentReads =
    let paymentPage (tx: ReadTx) (query: PaymentStorage.PageQuery) =
        let connection = tx.Connection
        let token = tx.CancellationToken
        PaymentStorage.page connection query token

    let latestPayments (tx: ReadTx) (parent, limit) =
        let connection = tx.Connection
        let token = tx.CancellationToken
        PaymentStorage.latest connection (parent, limit) token
