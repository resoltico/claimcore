namespace ClaimCore.Web

open ClaimCore.Contract
open ClaimCore.Contract.Protocol

open System
open System.IO
open System.Threading
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ClaimCore.Hosting

[<RequireQualifiedAccess>]
type BodyProblem =
    | BodyTooLarge
    | BodyCancelled
    | BodyUnreadable

/// HTTP allocation and transport failure handling; pure payload decoding belongs to Wire.
module HttpBody =
    let readBounded limit (stream: Stream) (ct: CancellationToken) =
        task {
            if limit < 1 then
                invalidArg (nameof limit) "The request byte limit must be positive."

            try
                use budget = CancellationTokenSource.CreateLinkedTokenSource(ct)
                budget.CancelAfter(TimeSpan.FromSeconds 10.)
                budget.Token.ThrowIfCancellationRequested()
                use output = new MemoryStream()
                let buffer = Array.zeroCreate<byte> 4096
                let mutable total = 0
                let mutable complete = false

                while total <= limit && not complete do
                    let! count = stream.ReadAsync(buffer, 0, buffer.Length, budget.Token)

                    if count = 0 then
                        complete <- true
                    elif count > limit - total then
                        total <- limit + 1
                    else
                        total <- total + count
                        output.Write(buffer, 0, count)

                budget.Token.ThrowIfCancellationRequested()

                if total > limit then
                    return Error BodyProblem.BodyTooLarge
                else
                    return Ok(output.ToArray())
            with
            | :? Microsoft.AspNetCore.Http.BadHttpRequestException as failure when
                failure.StatusCode = 413
                ->
                return Error BodyProblem.BodyTooLarge
            | :? OperationCanceledException -> return Error BodyProblem.BodyCancelled
            | :? IOException -> return Error BodyProblem.BodyUnreadable
        }

    let readRequest limit (context: HttpContext) =
        task {
            let services = context.RequestServices

            let stopping =
                if isNull (box services) then
                    CancellationToken.None
                else
                    services.GetService<IHostApplicationLifetime>()
                    |> Option.ofObj
                    |> Option.map _.ApplicationStopping
                    |> Option.defaultValue CancellationToken.None

            use linked =
                CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping)

            return! readBounded limit context.Request.Body linked.Token
        }
