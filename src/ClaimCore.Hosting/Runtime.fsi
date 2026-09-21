namespace ClaimCore.Hosting

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application

/// Local runtime composition. This is not a remote authentication or hostile-code sandbox.
[<Sealed>]
type Runtime =
    member Core: IClaimsCore

    static member OpenPostgres:
        connectionString: string * cancellationToken: CancellationToken ->
            Task<Result<Runtime, RuntimeOpenFault>>

    interface IDisposable
