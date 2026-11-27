namespace ClaimCore.Server.Composition

open ClaimCore.Domain

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Threading.Tasks
open System.Threading
open ClaimCore.Application
open ClaimCore.Server

/// A case-work host receives actor-bound operations and a bounded shutdown operation.
type internal IRuntime =
    inherit IDisposable

    abstract WithActor:
        PrincipalKey * CancellationToken * (ServiceContext -> Task<'Value>) -> Task<'Value option>

    abstract Housekeep: CancellationToken -> Task<ClaimCore.Application.HousekeepingReport>
    abstract Sessions: ClaimCore.Application.ISessionStore

    abstract RunDigests:
        WorkDateBasis *
        (EmailAddress -> Map<DigestCountKind, int64> -> CancellationToken -> Task<DigestRelayResult>) *
        (ActorId -> DigestOutcome -> unit) *
        CancellationToken ->
            Task<DigestRunSummary>

    abstract BusinessTimeZone: string
    abstract Ready: CancellationToken -> Task<bool>
    abstract RecoveryReady: CancellationToken -> Task<bool>
    abstract Shutdown: TimeSpan -> Task<ShutdownOutcome>

module internal ActorAccess =
    let run (gate: RuntimeLeases) forActor principal action =
        task {
            match gate.Acquire() with
            | None -> return None
            | Some lease ->
                use _lease = lease
                let! result = action (forActor principal)
                return Some result
        }

type private RuntimeResource(storage: ClaimCore.Application.IRuntime, calendar: IBusinessCalendar) =
    let capacity = AdmissionLease.limiter AdmissionPolicy.sessions 0

    let gate =
        RuntimeLeases(fun () ->
            try
                (capacity :> IDisposable).Dispose()
            finally
                storage.Dispose())

    let sessionAction action =
        task {
            match gate.Acquire() with
            | None -> return invalidOp "Runtime leases are closed."
            | Some lease ->
                use _lease = lease
                use admission = capacity.AttemptAcquire 1

                if not admission.IsAcquired then
                    return raise (AdmissionRejectedException())
                else
                    return! action storage.Sessions
        }

    let sessions =
        { new ClaimCore.Application.ISessionStore with
            member _.Create(principal, payload, expiry, idle, absolute, token) =
                sessionAction (fun store ->
                    store.Create(principal, payload, expiry, idle, absolute, token))

            member _.Retrieve(key, token) =
                sessionAction (fun store -> store.Retrieve(key, token))

            member _.Renew(key, principal, payload, validateExisting, token) =
                sessionAction (fun store ->
                    store.Renew(key, principal, payload, validateExisting, token))

            member _.Remove(key, reason, token) =
                sessionAction (fun store -> store.Remove(key, reason, token))
        }

    interface IRuntime with
        member _.WithActor(principal, cancellation, action) =
            let forActor actor =
                ClaimCore.Application.Runtime.service
                    storage
                    BuildIdentity.current.Version
                    calendar
                    actor

            task {
                let! admitted =
                    ActorAccess.run gate forActor principal (fun core ->
                        task {
                            let! active =
                                ClaimCore.Application.Runtime.recoveryReady storage cancellation

                            if not active then
                                return None
                            else
                                let! reply = action core
                                return Some reply
                        })

                return Option.flatten admitted
            }

        member _.Housekeep token =
            task {
                match gate.Acquire() with
                | None -> return raise (OperationCanceledException(token))
                | Some lease ->
                    use _lease = lease
                    return! storage.Housekeep(calendar.DateAt, token)
            }

        member _.RunDigests(basis, send, record, token) =
            task {
                match gate.Acquire() with
                | None -> return raise (OperationCanceledException(token))
                | Some lease ->
                    use _lease = lease
                    return! storage.RunDigests(basis, send, record, token)
            }

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

        member _.RecoveryReady token =
            task {
                match gate.Acquire() with
                | None -> return false
                | Some lease ->
                    use _lease = lease
                    return! ClaimCore.Application.Runtime.recoveryReady storage token
            }

        member _.Shutdown bound = gate.Shutdown bound
        member _.Dispose() = gate.Stop() |> ignore

module internal Runtime =
    let private openStorage operation =
        task {
            let! opened = operation

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
                    try
                        storage.Dispose()
                    with _ ->
                        ()

                    return Error RuntimeOpenFault.InstallationInvalid
        }

    let openConnection connection masterKey token =
        ClaimCore.Application.Runtime.openConnection connection masterKey token
        |> openStorage

    let openWitnessedConnection connection witness masterKey token =
        ClaimCore.Application.Runtime.openWitnessedConnection connection witness masterKey token
        |> openStorage
