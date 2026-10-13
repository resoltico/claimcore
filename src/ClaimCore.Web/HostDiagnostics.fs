namespace ClaimCore.Web

open System
open System.Collections.Generic
open System.Diagnostics.Metrics
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Contracts

[<RequireQualifiedAccess>]
type internal HostFailureStage =
    | BeforeDispatch
    | Dispatch
    | ResponseDelivery

[<RequireQualifiedAccess>]
type internal HostFailureCause =
    | Cancelled
    | Transport
    | Unexpected

[<NoEquality; NoComparison>]
type internal HostDiagnostic =
    {
        Id: string
        Endpoint: string
        Stage: HostFailureStage
        Cause: HostFailureCause
    }

/// Best effort, bounded and independent of core leases. Raw exceptions never enter this sink.
type internal HostDiagnosticSink(write: HostDiagnostic -> Task) =
    let meter = new Meter("ClaimCore.Web")
    let failures = meter.CreateCounter<int64>("claimcore.host.failures")
    let drops = meter.CreateCounter<int64>("claimcore.host.diagnostic_drops")
    let pending = Channel.CreateBounded<HostDiagnostic>(32)
    let stop = new CancellationTokenSource()
    let mutable dropped = 0L
    let mutable disposed = 0

    let drop () =
        Interlocked.Increment(&dropped) |> ignore
        drops.Add(1L)

    let worker =
        Task.Run(fun () ->
            task {
                try
                    while not stop.IsCancellationRequested do
                        let! event = pending.Reader.ReadAsync(stop.Token)

                        try
                            do! write event
                        with _ ->
                            drop ()

                        do! Task.Delay(TimeSpan.FromSeconds 1., stop.Token)
                with
                | :? OperationCanceledException when stop.IsCancellationRequested -> ()
                | :? ChannelClosedException -> ()
            }
            :> Task)

    new() =
        new HostDiagnosticSink(fun event ->
            Console.Error.WriteLineAsync(
                $"ClaimCore host failure: id={event.Id} endpoint={event.Endpoint} stage={event.Stage} cause={event.Cause}"
            ))

    member _.Report(event: HostDiagnostic) =
        try
            failures.Add(
                1L,
                KeyValuePair("endpoint", box event.Endpoint),
                KeyValuePair("stage", box (string event.Stage)),
                KeyValuePair("cause", box (string event.Cause))
            )

            if not (pending.Writer.TryWrite event) then
                drop ()
        with _ ->
            drop ()

    member _.Dropped = Interlocked.Read(&dropped)

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                stop.Cancel()
                pending.Writer.TryComplete() |> ignore
                // A blocked output owns only this bounded sink, never runtime pools or a request.
                if worker.Wait(TimeSpan.FromSeconds 1.) then
                    stop.Dispose()
                    meter.Dispose()
                else
                    worker.ContinueWith(fun (_: Task) ->
                        stop.Dispose()
                        meter.Dispose())
                    |> ignore

module internal HostDiagnostics =
    let private requestKey = obj ()
    let private endpoints = (ContractProjection.current ()).WebEndpoints

    let identify (context: HttpContext) =
        let id = Guid.NewGuid().ToString("N")
        context.Items[requestKey] <- id
        id

    let report (context: HttpContext) stage (error: exn) =
        let cause =
            match error with
            | :? OperationCanceledException -> HostFailureCause.Cancelled
            | :? IOException -> HostFailureCause.Transport
            | _ -> HostFailureCause.Unexpected

        let endpoint =
            endpoints
            |> List.tryFind (fun endpoint ->
                Some endpoint.Path = (context.Request.Path.Value |> Option.ofObj))
            |> Option.map _.Identifier
            |> Option.defaultValue "host"

        match context.Items.TryGetValue requestKey with
        | true, (:? string as id) ->
            context.RequestServices.GetService<HostDiagnosticSink>()
            |> Option.ofObj
            |> Option.iter (fun sink ->
                sink.Report
                    {
                        Id = id
                        Endpoint = endpoint
                        Stage = stage
                        Cause = cause
                    })
        | _ -> ()
