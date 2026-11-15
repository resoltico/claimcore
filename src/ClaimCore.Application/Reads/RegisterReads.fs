namespace ClaimCore.Application.Reads

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open Npgsql

module internal RegisterReads =
    let expiredBefore (tx: ReadTx) () =
        let connection = tx.Connection
        let token = tx.CancellationToken
        ExpiryFrontierStorage.read connection token

    let maintenance (tx: ReadTx) () =
        let connection = tx.Connection
        let token = tx.CancellationToken
        RegisterMaintenanceStore.read connection token
