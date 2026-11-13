namespace ClaimCore.Cli

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks

[<NoEquality; NoComparison>]
type internal TokenDispatch =
    {
        Utc: DateTimeOffset
        Timestamp: int64
    }

/// Bounds provider responses and records the actual token exchange's reuse-budget epoch.
type internal OidcBackchannel
    (inner: HttpMessageHandler, endpoint: unit -> Uri option, clock: TimeProvider) =
    inherit HttpMessageHandler()
    let transport = new HttpMessageInvoker(inner, false)
    let mutable dispatch: TokenDispatch option = None
    member _.Dispatch = dispatch

    override _.SendAsync(request, cancelled) =
        task {
            let token =
                request.Method = HttpMethod.Post
                && (match endpoint (), Option.ofObj request.RequestUri with
                    | Some expected, Some actual -> expected = actual
                    | _ -> false)

            if token then
                dispatch <-
                    Some
                        {
                            Utc = clock.GetUtcNow()
                            Timestamp = clock.GetTimestamp()
                        }

            let! response = transport.SendAsync(request, cancelled)

            try
                do!
                    response.Content.LoadIntoBufferAsync(
                        (if token then
                             int64 AccessTokens.MaximumResponseBytes
                         else
                             65536L),
                        cancelled
                    )

                return response
            with error ->
                response.Dispose()
                return raise error
        }

    override this.Dispose(disposing) =
        if disposing then
            transport.Dispose()

        base.Dispose(disposing)
