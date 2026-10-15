namespace ClaimCore.Hosting

open System
open System.Security.Cryptography
open System.Threading.Tasks
open System.Threading
open ClaimCore.Application

/// A case-work host receives actor-bound operations and a bounded shutdown operation.
type IRuntime =
    inherit IDisposable
    abstract Actor: PrincipalKey -> IActorClaimsCore
    abstract BusinessTimeZone: string
    abstract Ready: CancellationToken -> Task<bool>
    abstract Shutdown: TimeSpan -> Task<ShutdownOutcome>

type private RuntimeResource(storage: ClaimCore.Postgres.IRuntime, calendar: IBusinessCalendar) =
    let gate = AdmissionGate(fun () -> storage.Dispose())
    let cursorKey = RandomNumberGenerator.GetBytes 32

    interface IRuntime with
        member _.Actor principal =
            Core.create
                storage.Installation.InstallationId
                (ClaimCore.Postgres.Runtime.store storage)
                calendar
                cursorKey
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
    let openConnection connectionString cancellationToken =
        task {
            let! opened =
                ClaimCore.Postgres.Runtime.openConnection connectionString cancellationToken

            match opened with
            | Error error -> return Error error
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
