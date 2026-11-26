namespace ClaimCore.Server

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Net.Http
open System.Threading
open ClaimCore.Server.Composition

[<RequireQualifiedAccess>]
type internal OidcDependencyFailure =
    | Unavailable
    | Untrusted

type internal OidcDependencyException(failure: OidcDependencyFailure) =
    inherit Exception("OIDC trust evidence could not be admitted.")
    member _.Failure = failure

module internal OidcDependencyException =
    let rec tryFind (error: exn | null) =
        match error with
        | :? OidcDependencyException as failure -> Some failure.Failure
        | error when isNull error -> None
        | error -> tryFind ((nonNull error).InnerException)

/// The interactive and bearer handlers share one bounded issuer/configuration/key cache.
/// Freshness uses elapsed time; wall clock changes do not extend cached trust.
type internal IssuerMetadata
    (configuration: OidcConfiguration, client: HttpClient, clock: TimeProvider) =
    let gate = new SemaphoreSlim(1)
    let mutable cached = None
    let mutable checkedAt = 0L
    let mutable refresh = 0
    let lifetime = new CancellationTokenSource()

    let retrieve (uri: Uri) token =
        task {
            use! response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)

            if not response.IsSuccessStatusCode then
                let failure =
                    if int response.StatusCode >= 500 || int response.StatusCode = 429 then
                        OidcDependencyFailure.Unavailable
                    else
                        OidcDependencyFailure.Untrusted

                raise (OidcDependencyException failure)

            do! response.Content.LoadIntoBufferAsync(65536L, token)
            return! response.Content.ReadAsByteArrayAsync(token)
        }

    let fetch token =
        task {
            use bound = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token)
            bound.CancelAfter(TimeSpan.FromSeconds 5.)

            try
                let! bytes =
                    retrieve (OidcAuthority.discoveryLocation configuration.Issuer) bound.Token

                match
                    OidcAuthority.validateMetadata configuration.Issuer (ReadOnlyMemory bytes)
                with
                | Error _ -> return Error OidcDependencyFailure.Untrusted
                | Ok _ ->
                    let metadata =
                        Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration
                            .Create(Text.Encoding.UTF8.GetString bytes)

                    // Optional PAR is still controlled by the configured auth handler.
                    // Validate any advertised endpoint before a handler can use it.
                    if
                        not (String.IsNullOrEmpty metadata.PushedAuthorizationRequestEndpoint)
                        && Result.isError (
                            OidcAuthority.parse metadata.PushedAuthorizationRequestEndpoint
                        )
                    then
                        return Error OidcDependencyFailure.Untrusted
                    else
                        let! keys = retrieve (Uri metadata.JwksUri) bound.Token

                        let keySet =
                            Microsoft.IdentityModel.Tokens.JsonWebKeySet(
                                Text.Encoding.UTF8.GetString keys
                            )

                        let signing = keySet.GetSigningKeys()

                        if signing.Count = 0 then
                            return Error OidcDependencyFailure.Untrusted
                        else
                            for key in signing do
                                metadata.SigningKeys.Add key

                            return Ok metadata
            with
            | :? OperationCanceledException when token.IsCancellationRequested ->
                return raise (OperationCanceledException(token))
            | :? OidcDependencyException as error -> return Error error.Failure
            | :? HttpRequestException as error when
                error.HttpRequestError = HttpRequestError.SecureConnectionError
                ->
                return Error OidcDependencyFailure.Untrusted
            | :? HttpRequestException
            | :? OperationCanceledException -> return Error OidcDependencyFailure.Unavailable
            | :? ArgumentException
            | :? FormatException
            | :? Text.Json.JsonException
            | :? InvalidOperationException -> return Error OidcDependencyFailure.Untrusted
        }

    member _.Get(token: CancellationToken) =
        task {
            do! gate.WaitAsync token

            try
                let elapsed =
                    if cached.IsSome then
                        clock.GetElapsedTime checkedAt
                    else
                        TimeSpan.MaxValue

                let usable =
                    match cached with
                    | Some(Ok _) ->
                        elapsed < TimeSpan.FromMinutes 2.
                        && (Volatile.Read(&refresh) = 0 || elapsed < TimeSpan.FromSeconds 2.)
                    | Some(Error _) -> elapsed < TimeSpan.FromSeconds 2.
                    | None -> false

                if usable then
                    return cached.Value
                else
                    Interlocked.Exchange(&refresh, 0) |> ignore
                    let! result = fetch token
                    cached <- Some result
                    checkedAt <- clock.GetTimestamp()
                    return result
            finally
                gate.Release() |> ignore
        }

    member _.Client = client

    interface Microsoft.IdentityModel.Protocols.IConfigurationManager<
        Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration
               > with
        member this.GetConfigurationAsync token =
            task {
                match! this.Get token with
                | Ok value -> return value
                | Error failure -> return raise (OidcDependencyException failure)
            }

        member _.RequestRefresh() =
            Interlocked.Exchange(&refresh, 1) |> ignore

    interface IDisposable with
        member _.Dispose() =
            lifetime.Cancel()
            client.Dispose()
            lifetime.Dispose()
