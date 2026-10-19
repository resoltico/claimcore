namespace ClaimCore.Cli

open System
open System.Net.Http
open System.Threading
open Duende.IdentityModel.OidcClient

module InteractiveAuthorization =
    let private tokenEndpoint (options: OidcClientOptions) () =
        match options.ProviderInformation with
        | null -> None
        | provider ->
            match Uri.TryCreate(provider.TokenEndpoint, UriKind.Absolute) with
            | true, uri -> Option.ofObj uri
            | _ -> None

    let internal configuration (issuer: Uri) clientId (browser: LoopbackBrowser) =
        OidcClientOptions(
            Authority = issuer.AbsoluteUri,
            ClientId = clientId,
            Scope = "openid",
            RedirectUri = browser.RedirectUri,
            Browser = browser,
            LoadProfile = false,
            BackchannelTimeout = TimeSpan.FromSeconds 20.
        )

    let private received
        clock
        (browser: LoopbackBrowser)
        (transport: OidcBackchannel)
        (result: LoginResult)
        =
        if result.IsError then
            Error(
                browser.Failure
                |> Option.defaultValue (
                    if transport.Dispatch.IsSome then
                        "OIDC_TOKEN_UNAVAILABLE"
                    else
                        "OIDC_CALLBACK_INVALID"
                )
            )
        else
            match transport.Dispatch with
            | None -> Error "OIDC_TOKEN_UNAVAILABLE"
            | Some dispatch ->
                AccessTokens.fromResponseAt
                    clock
                    dispatch.Utc
                    dispatch.Timestamp
                    result.TokenResponse

    let obtain (handler: HttpMessageHandler) (issuer: Uri) clientId (cancelled: CancellationToken) =
        task {
            use browser = new LoopbackBrowser()
            let options = configuration issuer clientId browser
            let clock = TimeProvider.System
            use transport = new OidcBackchannel(handler, tokenEndpoint options, clock)
            use client = new HttpClient(transport, false)
            client.Timeout <- options.BackchannelTimeout
            client.MaxResponseContentBufferSize <- 65536L
            options.BackchannelHandler <- transport
            options.HttpClientFactory <- Func<OidcClientOptions, HttpClient>(fun _ -> client)
            let oidc = Duende.IdentityModel.OidcClient.OidcClient options

            try
                let! result = oidc.LoginAsync(LoginRequest(), cancelled)
                return received clock browser transport result
            with
            | :? OperationCanceledException
            | :? HttpRequestException
            | :? IO.IOException -> return Error "OIDC_TOKEN_UNAVAILABLE"
            | :? InvalidOperationException
            | :? ArgumentException -> return Error "OIDC_METADATA_INVALID"
        }
