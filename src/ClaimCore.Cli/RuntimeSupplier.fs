namespace ClaimCore.Cli

open System
open System.Threading
open ClaimCore.Hosting

/// The CLI composition root. This is the only CLI type that names a runtime factory, and it owns
/// the opened lifetime for the process. Opening is deferred until an invocation actually needs a
/// core, so database-free commands and rejected frames never reach PostgreSQL.
type RuntimeSupplier() =
    let mutable runtime: Runtime option = None

    interface ICoreSupplier with
        member _.Acquire(cancellationToken: CancellationToken) =
            task {
                match runtime with
                | Some active -> return Ok active.Core
                | None ->
                    match PrivateFiles.connection () with
                    | Error message -> return Error(CoreUnavailable.Configuration message)
                    | Ok connection ->
                        match! Runtime.OpenPostgres(connection, cancellationToken) with
                        | Error fault -> return Error(CoreUnavailable.Open fault)
                        | Ok opened ->
                            runtime <- Some opened
                            return Ok opened.Core
            }

    interface IDisposable with
        member _.Dispose() =
            runtime |> Option.iter (fun active -> (active :> IDisposable).Dispose())
            runtime <- None
