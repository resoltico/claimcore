namespace ClaimCore.Hosting

open System
open System.Threading.Tasks
open System.Threading
open ClaimCore.Application

[<RequireQualifiedAccess>]
type RuntimeOpenFault =
    | InvalidTransport
    | UnsupportedServer
    | UnsafeRole
    | SchemaMismatch
    | InstallationInvalid
    | HeadMismatch
    | Unavailable
    | Cancelled

/// A case-work host receives actor-bound operations and a bounded shutdown operation.
type IRuntime =
    inherit IDisposable
    abstract Actor: PrincipalKey -> IActorClaimsCore
    abstract BusinessTimeZone: string
    abstract Ready: CancellationToken -> Task<bool>
    abstract Shutdown: TimeSpan -> Task<ShutdownOutcome>

type private RuntimeResource(storage: ClaimCore.Postgres.IRuntime, calendar: IBusinessCalendar) =
    let gate = AdmissionGate(fun () -> storage.Dispose())

    interface IRuntime with
        member _.Actor principal =
            Core.create
                storage.Installation.InstallationId
                (ClaimCore.Postgres.Runtime.store storage)
                calendar
                principal
            |> ActorFacade.bind gate

        member _.BusinessTimeZone = calendar.TimeZoneId

        member _.Ready token =
            task {
                match gate.Acquire() with
                | None -> return false
                | Some lease ->
                    use _lease = lease
                    return! storage.Ready token
            }

        member _.Shutdown bound = gate.Shutdown bound
        member _.Dispose() = gate.Stop() |> ignore

module Runtime =
    let internal fault =
        function
        | ClaimCore.Postgres.RuntimeOpenFault.InvalidTransport -> RuntimeOpenFault.InvalidTransport
        | ClaimCore.Postgres.RuntimeOpenFault.UnsupportedServer ->
            RuntimeOpenFault.UnsupportedServer
        | ClaimCore.Postgres.RuntimeOpenFault.UnsafeRole -> RuntimeOpenFault.UnsafeRole
        | ClaimCore.Postgres.RuntimeOpenFault.SchemaMismatch -> RuntimeOpenFault.SchemaMismatch
        | ClaimCore.Postgres.RuntimeOpenFault.InstallationInvalid ->
            RuntimeOpenFault.InstallationInvalid
        | ClaimCore.Postgres.RuntimeOpenFault.HeadMismatch -> RuntimeOpenFault.HeadMismatch
        | ClaimCore.Postgres.RuntimeOpenFault.Unavailable -> RuntimeOpenFault.Unavailable
        | ClaimCore.Postgres.RuntimeOpenFault.Cancelled -> RuntimeOpenFault.Cancelled

    let openConnection connectionString cancellationToken =
        task {
            let! opened =
                ClaimCore.Postgres.Runtime.openConnection connectionString cancellationToken

            match opened with
            | Error error -> return Error(fault error)
            | Ok storage ->
                try
                    return
                        Ok(
                            new RuntimeResource(
                                storage,
                                BusinessCalendar.resolve storage.Installation.BusinessTimeZone
                            )
                            :> IRuntime
                        )
                with
                | :? TimeZoneNotFoundException
                | :? InvalidTimeZoneException ->
                    storage.Dispose()
                    return Error RuntimeOpenFault.InstallationInvalid
        }
