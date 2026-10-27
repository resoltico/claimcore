module internal ClaimCore.AcceptanceTests.HttpsProxy

open ClaimCore.Domain
open ClaimCore.Protocol


open System
open System.Net
open System.Net.Http
open System.Security.Cryptography.X509Certificates
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging

let private request (backend: string) (context: HttpContext) =
    let value =
        new HttpRequestMessage(
            HttpMethod context.Request.Method,
            backend + context.Request.Path.Value + context.Request.QueryString.Value
        )

    if
        context.Request.ContentLength.GetValueOrDefault() > 0L
        || context.Request.Headers.ContainsKey "Transfer-Encoding"
    then
        value.Content <- new StreamContent(context.Request.Body)

    for header in context.Request.Headers do
        if not (value.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) then
            match value.Content with
            | null -> ()
            | content ->
                content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())
                |> ignore

    value.Headers.Host <- context.Request.Host.Value
    value

let private copyHeaders (response: HttpResponseMessage) (context: HttpContext) =
    for header in Seq.append response.Headers response.Content.Headers do
        if
            not (
                [ "Transfer-Encoding"; "Connection"; "Content-Length" ]
                |> List.exists (fun name ->
                    String.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            )
        then
            context.Response.Headers[header.Key] <-
                Microsoft.Extensions.Primitives.StringValues(Seq.toArray header.Value)

let private relay
    (http: HttpClient)
    backend
    (control: RedirectControl.Control)
    (context: HttpContext)
    =
    task {
        let path = context.Request.Path.Value |> nonNull
        let armed = control.Arm path

        if path = "/__synthetic/redirect-destination" then
            control.Destination()
            context.Response.ContentType <- "application/json"
            do! context.Response.WriteAsync("{}")
        elif path = "/__synthetic/redirect-control" then
            control.Original None
            context.Response.StatusCode <- armed |> Option.defaultValue 200
            context.Response.Headers.Location <- "/__synthetic/redirect-destination"
        elif armed = Some RedirectControl.beforeDispatchFailure then
            control.Original None

            context.Response.StatusCode <-
                HostFailureReason.status HostFailureReason.BeforeDispatchFailed

            context.Response.ContentType <- "application/json"

            do!
                context.Response.Body.WriteAsync(
                    HostFailureCodecs.encode HostFailureReason.BeforeDispatchFailed
                )
        else
            use request = request backend context

            use! response =
                http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    context.RequestAborted
                )

            copyHeaders response context

            match armed with
            | Some status ->
                let! bytes = response.Content.ReadAsByteArrayAsync(context.RequestAborted)
                control.Original(Some bytes)

                if status = RedirectControl.lostResponse then
                    context.Abort()
                else
                    context.Response.StatusCode <- status
                    context.Response.Headers.Location <- "/__synthetic/redirect-destination"
                    context.Response.ContentLength <- Nullable 0L
            | None ->
                context.Response.StatusCode <- int response.StatusCode
                do! response.Content.CopyToAsync(context.Response.Body, context.RequestAborted)
    }

type Proxy
    (
        origin: string,
        backend: string,
        certificate: X509Certificate2,
        http: HttpClient,
        directory: string
    ) =
    let builder = WebApplication.CreateBuilder()
    do builder.Logging.ClearProviders() |> ignore

    do
        builder.WebHost.ConfigureKestrel(fun options ->
            options.Listen(
                IPAddress.Loopback,
                Uri(origin).Port,
                fun endpoint -> endpoint.UseHttps(certificate) |> ignore
            ))
        |> ignore

    let application = builder.Build()
    let control = RedirectControl.Control(directory)
    let routing = obj ()
    let mutable selectedBackend = backend

    let selected () =
        lock routing (fun () -> selectedBackend)

    do application.Run(RequestDelegate(fun context -> relay http (selected ()) control context))
    member _.Start() = application.StartAsync()
    // Fixture routing changes only new requests; dispatched requests retain their chosen host.
    member _.RouteTo(backend: string) =
        lock routing (fun () -> selectedBackend <- backend)

    interface IAsyncDisposable with
        member _.DisposeAsync() = application.DisposeAsync()
