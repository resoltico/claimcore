namespace ClaimCore.Web.Composition

open ClaimCore.Domain

open ClaimCore.Protocol

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application

/// The HTTPS composition supplies actor-bound Application operations.
type IServiceRuntime =
    inherit IDisposable
    abstract WithActor: PrincipalKey * (ServiceContext -> Task<'Value>) -> Task<'Value option>
    abstract Sessions: ClaimCore.Postgres.ISessionStore
    abstract Ready: CancellationToken -> Task<bool>
    abstract Shutdown: TimeSpan -> Task<unit>

module ServiceRuntime =
    let openConnection connectionString cancellationToken =
        task {
            match! Runtime.openConnection connectionString cancellationToken with
            | Error value -> return Error(string value)
            | Ok runtime ->
                return
                    Ok
                        { new IServiceRuntime with
                            member _.WithActor(principal, action) =
                                runtime.WithActor(principal, action)

                            member _.Sessions = runtime.Sessions

                            member _.Ready token = runtime.Ready token

                            member _.Shutdown bound =
                                task { let! _ = runtime.Shutdown bound in return () }

                            member _.Dispose() = runtime.Dispose()
                        }
        }

/// Project release metadata.
module ServiceIdentity =
    let product = BuildIdentity.current.Product
    let version = BuildIdentity.current.Version
    let assemblyVersion = BuildIdentity.current.AssemblyVersion
    let fileVersion = BuildIdentity.current.FileVersion
