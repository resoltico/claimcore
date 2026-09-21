namespace ClaimCore.Hosting

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application

/// The source is borrowed only after successful adoption; every other opening path closes it.
module internal RuntimeSourceOwnership =
    let openOwned<'source, 'core, 'runtime when 'source :> IDisposable>
        (create: unit -> 'source)
        (initialize: 'source -> Task<Result<'core, RuntimeOpenFault>>)
        (adopt: 'core -> 'source -> 'runtime)
        (fault: exn -> RuntimeOpenFault)
        (cancellationToken: CancellationToken)
        =
        task {
            let mutable borrowedSource: 'source option = None

            try
                try
                    cancellationToken.ThrowIfCancellationRequested()
                    let source = create ()
                    borrowedSource <- Some source
                    let! opened = initialize source

                    match opened with
                    | Error reason -> return Error reason
                    | Ok core ->
                        cancellationToken.ThrowIfCancellationRequested()
                        let runtime = adopt core source
                        borrowedSource <- None
                        return Ok runtime
                with error ->
                    return Error(fault error)
            finally
                match borrowedSource with
                | Some source ->
                    try
                        source.Dispose()
                    with _ ->
                        ()
                | None -> ()
        }
