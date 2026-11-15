namespace ClaimCore.Postgres

open System
open System.Diagnostics
open System.Threading
open Npgsql
open ClaimCore.Application.Contracts

/// A bounded diagnostic value. It deliberately has no exception, message, SQL or user-data field.
[<RequireQualifiedAccess>]
type FailurePhase =
    | Open
    | Read
    | Head
    | Append
    | Projection
    | Commit
    | Session
    | Housekeeping
    | Cleanup

[<RequireQualifiedAccess>]
type FailureClass =
    | Cancelled
    | Timeout
    | Provider
    | InputOutput
    | Installation
    | Internal

[<NoEquality; NoComparison>]
type FailureDiagnostic =
    {
        Correlation: Guid
        RequestCorrelation: Guid option
        Phase: FailurePhase
        Classification: FailureClass
        Methods: string array
    }

module internal FailureDiagnostics =
    type private Context =
        {
            Correlation: Guid
            mutable Phase: FailurePhase
            mutable Reported: bool
        }

    let correlation = AsyncLocal<Guid option>()

    let private observers =
        Collections.Concurrent.ConcurrentDictionary<Guid, FailureDiagnostic -> unit>()

    let subscribe observer =
        let key = Guid.NewGuid()
        observers[key] <- observer

        { new IDisposable with
            member _.Dispose() = observers.TryRemove key |> ignore
        }

    let private current = AsyncLocal<Context option>()
    let listener = new DiagnosticListener("ClaimCore.Storage.Failures")

    let scope phase =
        let previous = current.Value

        current.Value <-
            Some
                {
                    Correlation = Guid.NewGuid()
                    Phase = phase
                    Reported = false
                }

        { new IDisposable with
            member _.Dispose() = current.Value <- previous
        }

    let phase value =
        current.Value |> Option.iter (fun context -> context.Phase <- value)

    let reportAt phase (error: exn) =
        // Observer failures must never affect business results or provider cleanup.
        try
            let context = current.Value

            if phase = FailurePhase.Cleanup || not (context |> Option.exists _.Reported) then
                if phase <> FailurePhase.Cleanup then
                    context |> Option.iter (fun value -> value.Reported <- true)

                let methods =
                    if error :? NpgsqlException then
                        [||]
                    else
                        StackTrace(error, false).GetFrames()
                        |> Array.choose (fun frame ->
                            match frame.GetMethod() with
                            | null -> None
                            | method ->
                                match method.DeclaringType with
                                | null -> None
                                | owner when owner.Assembly = typeof<FailureDiagnostic>.Assembly ->
                                    match owner.FullName with
                                    | null -> None
                                    | name ->
                                        let value = name + "." + method.Name
                                        Some(value.Substring(0, min 160 value.Length))
                                | _ -> None)
                        |> Array.truncate 8

                let value =
                    {
                        Correlation =
                            current.Value
                            |> Option.map _.Correlation
                            |> Option.defaultWith Guid.NewGuid
                        RequestCorrelation = correlation.Value
                        Phase = phase
                        Classification =
                            match error with
                            | :? InstallationConfigurationException -> FailureClass.Installation
                            | :? OperatorFaultException -> FailureClass.Installation
                            | :? OperationCanceledException -> FailureClass.Cancelled
                            | :? TimeoutException -> FailureClass.Timeout
                            | :? NpgsqlException -> FailureClass.Provider
                            | :? IO.IOException -> FailureClass.InputOutput
                            | _ -> FailureClass.Internal
                        Methods = methods
                    }

                for observer in observers.Values do
                    try
                        observer value
                    with _ ->
                        ()

                listener.Write("Failure", value)
        with _ ->
            ()

    let report (error: exn) =
        match error with
        | :? MaintenanceRequiredException -> ()
        | :? OperatorFaultException as refusal when
            List.contains
                refusal.Fault
                [
                    OperatorFault.AlreadyInitialized
                    OperatorFault.InitialOwnerInvalid
                    OperatorFault.TimeZoneInvalid
                ]
            ->
            ()
        | _ ->
            reportAt
                (current.Value |> Option.map _.Phase |> Option.defaultValue FailurePhase.Open)
                error

    /// Synchronous owner disposal follows the same bounded reporting policy as async cleanup.
    let cleanupSync dispose =
        try
            dispose ()
        with error ->
            reportAt FailurePhase.Cleanup error

    /// Cleanup cannot overwrite either a confirmed result or the primary failure.
    let cleanup (dispose: unit -> Threading.Tasks.ValueTask) =
        task {
            try
                do! dispose ()
            with error ->
                reportAt FailurePhase.Cleanup error
        }

/// Production hosts consume only sanitized values; no provider or exception crosses this seam.
module StorageDiagnostics =
    let subscribe observer = FailureDiagnostics.subscribe observer

    let correlate id =
        let previous = FailureDiagnostics.correlation.Value
        FailureDiagnostics.correlation.Value <- Some id

        { new IDisposable with
            member _.Dispose() =
                FailureDiagnostics.correlation.Value <- previous
        }
