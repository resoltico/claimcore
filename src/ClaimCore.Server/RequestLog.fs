namespace ClaimCore.Server

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open Microsoft.Extensions.Logging
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Server.Composition

[<NoEquality; NoComparison>]
type internal RequestLogEntry =
    {
        Endpoint: string
        MetricEndpoint: EndpointId option
        CsvExport: bool
        mutable MetricResult: RequestMetricResult
        mutable Outcome: string
        mutable ActorId: ActorId option
        mutable OperationId: OperationId option
    }

module internal RequestLog =
    let private key = obj ()

    let start endpoint (context: HttpContext) =
        let csvExport = Option.ofObj context.Request.Path.Value = Some ExportTransport.path

        let entry =
            {
                Endpoint =
                    if csvExport then
                        ExportTransport.token
                    else
                        match
                            Option.ofObj context.Request.Path.Value
                            |> Option.bind AttachmentTransport.tryPath
                        with
                        | Some transfer -> AttachmentTransport.token transfer
                        | None ->
                            endpoint
                            |> Option.map EndpointIds.token
                            |> Option.defaultValue "unknown"
                MetricEndpoint = endpoint
                CsvExport = csvExport
                MetricResult = RequestMetricResult.HostFailure
                Outcome = "HOST_FAILURE"
                ActorId = None
                OperationId = None
            }

        context.Items[key] <- entry
        entry

    let private entry (context: HttpContext) =
        match context.Items.TryGetValue key with
        | true, (:? RequestLogEntry as value) -> Some value
        | _ -> None

    let hostFailure context =
        entry context
        |> Option.iter (fun value ->
            value.Outcome <- "HOST_FAILURE"
            value.MetricResult <- RequestMetricResult.HostFailure)

    let reply classification context =
        entry context
        |> Option.iter (fun value ->
            value.Outcome <- ReplyKind.token classification
            value.MetricResult <- WebMetrics.result classification)

    let operation id context =
        OperationId.create id
        |> Option.iter (fun id ->
            entry context |> Option.iter (fun value -> value.OperationId <- Some id))

    let actor id context =
        ActorId.create id
        |> Option.iter (fun id ->
            entry context |> Option.iter (fun value -> value.ActorId <- Some id))

    let exceptionLine (error: exn) =
        let frames = StackTrace(error, false).GetFrames()

        let stack =
            frames
            |> Array.choose (fun frame ->
                match frame.GetMethod() |> Option.ofObj with
                | None -> None
                | Some method ->
                    let owner =
                        method.DeclaringType
                        |> Option.ofObj
                        |> Option.bind (fun value -> Option.ofObj value.FullName)
                        |> Option.defaultValue ""

                    Some(owner + "." + method.Name))

        JsonSerializer.Serialize
            {|
                kind = "exception"
                exceptionType = error.GetType().FullName
                stack = stack
            |}

    let reportException (sink: TextWriter) error =
        try
            sink.WriteLine(exceptionLine error)
        with _ ->
            ()

    let middleware (sink: TextWriter) (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            let correlation = Guid.NewGuid()
            use _correlation = ClaimCore.Application.Runtime.correlate correlation

            if context.Request.Path.StartsWithSegments(PathString "/api") then
                let endpoint =
                    Api.all
                    |> List.tryFind (fun e ->
                        Some(Api.path e) = Option.ofObj context.Request.Path.Value)
                    |> Option.map _.Id

                let value = start endpoint context
                let started = Stopwatch.GetTimestamp()

                try
                    do! next.Invoke context
                finally
                    let elapsed = Stopwatch.GetElapsedTime started
                    let duration = elapsed.TotalMilliseconds

                    if value.CsvExport then
                        WebMetrics.csvExport value.MetricResult elapsed
                    else
                        WebMetrics.request value.MetricEndpoint value.MetricResult elapsed

                    let line =
                        JsonSerializer.Serialize
                            {|
                                correlation = correlation
                                endpoint = value.Endpoint
                                outcome = value.Outcome
                                status = context.Response.StatusCode
                                durationMs = duration
                                actorId =
                                    value.ActorId |> Option.map ActorId.value |> Option.toNullable
                                operationId =
                                    value.OperationId
                                    |> Option.map OperationId.value
                                    |> Option.toNullable
                            |}

                    try
                        sink.WriteLine line
                    with _ ->
                        ()
            else
                do! next.Invoke context
        }

/// Framework failures can occur before or after the owned request delegate.
/// Retain only the same safe exception representation, never framework state or formatted text.
type internal FrameworkExceptionLog(sink: TextWriter) =
    let enabled level =
        level = LogLevel.Error || level = LogLevel.Critical

    let scope =
        { new IDisposable with
            member _.Dispose() = ()
        }

    let logger =
        { new ILogger with
            member _.BeginScope<'State when 'State: not null>(_: 'State) = scope
            member _.IsEnabled level = enabled level

            member _.Log<'State>(level, _, _: 'State, error, _) =
                if enabled level then
                    error |> Option.ofObj |> Option.iter (RequestLog.reportException sink)
        }

    interface ILoggerProvider with
        member _.CreateLogger _ = logger

    interface IDisposable with
        member _.Dispose() = ()
