namespace ClaimCore.Web

open ClaimCore.Domain
open ClaimCore.Protocol

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Web.Composition

[<NoEquality; NoComparison>]
type internal RequestLogEntry =
    {
        Endpoint: string
        mutable Outcome: string
        mutable ActorId: ActorId option
        mutable OperationId: OperationId option
    }

module internal RequestLog =
    let private key = obj ()

    let start endpoint (context: HttpContext) =
        let entry =
            {
                Endpoint = endpoint
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

    let outcome tag context =
        entry context |> Option.iter (fun value -> value.Outcome <- tag)

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
            if context.Request.Path.StartsWithSegments(PathString "/api") then
                let endpoint =
                    Api.all
                    |> List.tryFind (fun e ->
                        Some(Api.path e) = Option.ofObj context.Request.Path.Value)
                    |> Option.map Api.id
                    |> Option.defaultValue "unknown"

                let value = start endpoint context
                let started = Stopwatch.GetTimestamp()

                try
                    do! next.Invoke context
                finally
                    let duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds

                    let line =
                        JsonSerializer.Serialize
                            {|
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
