namespace ClaimCore.Cli

open System
open System.Net.Http
open System.Threading
open Duende.IdentityModel.OidcClient

module InteractiveAuthorization =
    [<NoEquality; NoComparison>]
    type internal Interaction =
        {
            Clock: TimeProvider
            Launch: string -> Result<unit, AccessFailure>
        }

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
        endpoint
        (result: LoginResult)
        =
        if result.IsError then
            Error(
                browser.Failure
                |> Option.defaultValue (
                    if transport.Dispatch.IsSome then
                        AccessFailure.OidcTokenUnavailable
                    else
                        AccessFailure.OidcCallbackInvalid
                )
            )
        else
            match transport.Dispatch with
            | None -> Error AccessFailure.OidcTokenUnavailable
            | Some dispatch ->
                AuthorizationGrant.fromResponse
                    {
                        Clock = clock
                        Utc = dispatch.Utc
                        Timestamp = dispatch.Timestamp
                    }
                    endpoint
                    None
                    result.TokenResponse

    let internal obtainWith
        interaction
        (handler: HttpMessageHandler)
        (issuer: Uri)
        clientId
        (cancelled: CancellationToken)
        =
        task {
            use browser = new LoopbackBrowser(interaction.Launch)
            let options = configuration issuer clientId browser
            let clock = interaction.Clock
            use transport = new OidcBackchannel(handler, tokenEndpoint options, clock)
            use client = new HttpClient(transport, false)
            client.Timeout <- options.BackchannelTimeout
            client.MaxResponseContentBufferSize <- 65536L
            options.BackchannelHandler <- transport
            options.HttpClientFactory <- Func<OidcClientOptions, HttpClient>(fun _ -> client)
            let oidc = Duende.IdentityModel.OidcClient.OidcClient options

            try
                let! result = oidc.LoginAsync(LoginRequest(), cancelled)

                try
                    match tokenEndpoint options () with
                    | Some endpoint -> return received clock browser transport endpoint result
                    | None -> return Error AccessFailure.OidcMetadataInvalid
                finally
                    result.RefreshTokenHandler |> Option.ofObj |> Option.iter _.Dispose()
            with
            | :? OperationCanceledException
            | :? HttpRequestException
            | :? IO.IOException -> return Error AccessFailure.OidcTokenUnavailable
            | :? InvalidOperationException
            | :? ArgumentException -> return Error AccessFailure.OidcMetadataInvalid
        }

    let internal obtain handler issuer clientId cancelled =
        obtainWith
            {
                Clock = TimeProvider.System
                Launch = LoopbackTransport.launch
            }
            handler
            issuer
            clientId
            cancelled
