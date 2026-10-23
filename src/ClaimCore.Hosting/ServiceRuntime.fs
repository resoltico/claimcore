namespace ClaimCore.Hosting

open ClaimCore.Contract

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application

/// Public host runtime: typed core objects remain behind the protocol service.
type IServiceRuntime =
    inherit IDisposable
    abstract Service: IProtocolService
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
                            member _.Service = ServiceGateway.bind runtime.Actor
                            member _.Ready token = runtime.Ready token

                            member _.Shutdown bound =
                                task { let! _ = runtime.Shutdown bound in return () }

                            member _.Dispose() = runtime.Dispose()
                        }
        }

/// Project release metadata is exposed without exporting Application types to the host.
module ServiceIdentity =
    let product = BuildIdentity.current.Product
    let version = BuildIdentity.current.Version
    let assemblyVersion = BuildIdentity.current.AssemblyVersion
    let fileVersion = BuildIdentity.current.FileVersion
