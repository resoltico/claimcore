namespace ClaimCore.Web.Composition

open ClaimCore.Contract

open System
open System.Threading.Tasks
open System.Threading
open ClaimCore.Application

/// A case-work host receives actor-bound operations and a bounded shutdown operation.
type internal IRuntime =
    inherit IDisposable
    abstract WithActor: PrincipalKey * (HandlerContext -> Task<'Value>) -> Task<'Value option>
    abstract Sessions: ClaimCore.Postgres.ISessionStore
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

    let sessionAction action =
        task {
            match gate.Acquire() with
            | None -> return invalidOp "Runtime admission is closed."
            | Some lease ->
                use _lease = lease
                return! action storage.Sessions
        }

    let sessions =
        { new ClaimCore.Postgres.ISessionStore with
            member _.Create(payload, expiry, idle, absolute, token) =
                sessionAction (fun store -> store.Create(payload, expiry, idle, absolute, token))

            member _.Retrieve(key, token) =
                sessionAction (fun store -> store.Retrieve(key, token))

            member _.Renew(key, payload, token) =
                sessionAction (fun store -> store.Renew(key, payload, token))

            member _.Remove(key, token) =
                sessionAction (fun store -> store.Remove(key, token))
        }

    interface IRuntime with
        member _.WithActor(principal, action) =
            ActorAccess.run
                gate
                (fun actor ->
                    ClaimCore.Application.Handlers.context
                        storage.Installation.InstallationId
                        (ClaimCore.Postgres.Runtime.store storage)
                        calendar
                        actor)
                principal
                action


        member _.Sessions = sessions

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
