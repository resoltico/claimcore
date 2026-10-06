namespace ClaimCore.Hosting

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application

/// Local runtime composition. This is not a remote authentication or hostile-code sandbox.
[<Sealed>]
type Runtime =
    member ForActor: principal: PrincipalKey -> IActorClaimsCore

    member internal ForActorWithAdmission:
        principal: PrincipalKey * selectedAdmission: RuntimeAdmission -> IActorClaimsCore

    member DataUseReadiness: ?cancellationToken: CancellationToken -> Task<string * string * bool>

    static member internal OpenPostgres:
        connectionString: string *
        witnessConnection: string *
        witnessKey: byte array *
        suppressionKeyFilePath: string *
        artifactKeyRingPath: string *
        cancellationToken: CancellationToken ->
            Task<Result<Runtime, RuntimeOpenFault>>

    static member OpenPostgres:
        connectionString: string *
        witnessConnection: string *
        witnessKeyRingFilePath: string *
        suppressionKeyFilePath: string *
        artifactKeyRingPath: string *
        cancellationToken: CancellationToken ->
            Task<Result<Runtime, RuntimeOpenFault>>

    interface IDisposable
    interface IAsyncDisposable
