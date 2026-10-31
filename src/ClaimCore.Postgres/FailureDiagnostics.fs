namespace ClaimCore.Postgres

open System
open System.Diagnostics
open System.Threading
open Npgsql

/// A bounded diagnostic value. It deliberately has no exception, message, SQL or user-data field.
[<RequireQualifiedAccess>]
type internal FailurePhase =
    | Open
    | Read
    | Head
    | Append
    | Projection
    | Commit
    | Cleanup

[<RequireQualifiedAccess>]
type internal FailureClass =
    | Cancelled
    | Timeout
    | Provider
    | InputOutput
    | Internal

[<NoEquality; NoComparison>]
type internal FailureDiagnostic =
    {
        Correlation: Guid
        Phase: FailurePhase
        Classification: FailureClass
        Methods: string array
    }

module internal FailureDiagnostics =
    type private Context =
        {
            Correlation: Guid
            mutable Phase: FailurePhase
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
                }

        { new IDisposable with
            member _.Dispose() = current.Value <- previous
        }

    let phase value =
        current.Value |> Option.iter (fun context -> context.Phase <- value)

    let reportAt phase (error: exn) =
        // Observer failures must never affect business results or provider cleanup.
        try
            if listener.IsEnabled "Failure" then
                let methods =
                    if error :? NpgsqlException then
                        [||]
                    else
                        StackTrace(error, false).GetFrames()
                        |> Array.truncate 8
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

                let value =
                    {
                        Correlation =
                            current.Value
                            |> Option.map _.Correlation
                            |> Option.defaultWith Guid.NewGuid
                        Phase = phase
                        Classification =
                            match error with
                            | :? OperationCanceledException -> FailureClass.Cancelled
                            | :? TimeoutException -> FailureClass.Timeout
                            | :? NpgsqlException -> FailureClass.Provider
                            | :? IO.IOException -> FailureClass.InputOutput
                            | _ -> FailureClass.Internal
                        Methods = methods
                    }

                listener.Write("Failure", value)
        with _ ->
            ()

    let report error =
        reportAt
            (current.Value |> Option.map _.Phase |> Option.defaultValue FailurePhase.Open)
            error

    /// Cleanup cannot overwrite either a confirmed result or the primary failure.
    let cleanup (dispose: unit -> Threading.Tasks.ValueTask) =
        task {
            try
                do! dispose ()
            with error ->
                reportAt FailurePhase.Cleanup error
        }
