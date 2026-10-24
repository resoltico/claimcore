namespace ClaimCore.Web.Composition

open ClaimCore.Contract

open System
open System.Threading.Tasks
open System.Threading
open ClaimCore.Application

/// A case-work host receives actor-bound operations and a bounded shutdown operation.
type internal IRuntime =
    inherit IDisposable
    abstract WithActor: PrincipalKey * (IActorClaimsCore -> Task<'Value>) -> Task<'Value option>
    abstract BusinessTimeZone: string
    abstract Ready: CancellationToken -> Task<bool>
    abstract Shutdown: TimeSpan -> Task<ShutdownOutcome>

module internal ActorAccess =
    let run (gate: AdmissionGate) forActor principal action =
        task {
            match gate.Acquire() with
            | None -> return None
            | Some lease ->
                use _lease = lease
                let! result = action (forActor principal)
                return Some result
        }

type private RuntimeResource(storage: ClaimCore.Postgres.IRuntime, calendar: IBusinessCalendar) =
    let gate = AdmissionGate(fun () -> storage.Dispose())

    interface IRuntime with
        member _.WithActor(principal, action) =
            ActorAccess.run
                gate
                (fun actor ->
                    ClaimCore.Application.Core.create
                        storage.Installation.InstallationId
                        (ClaimCore.Postgres.Runtime.store storage)
                        calendar
                        actor)
                principal
                action

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

module internal Runtime =
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
