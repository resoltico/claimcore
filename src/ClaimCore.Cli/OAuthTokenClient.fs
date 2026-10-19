namespace ClaimCore.Cli

open System
open System.Net.Http
open System.Threading
open Duende.IdentityModel.Client

module OAuthTokenClient =
    let internal acquireWithClock
        (clock: TimeProvider)
        (client: HttpClient)
        (endpoint: Uri)
        clientId
        secretFile
        (cancelled: CancellationToken)
        =
        task {
            if endpoint.Scheme <> Uri.UriSchemeHttps then
                return Error "OIDC_TOKEN_ENDPOINT_INVALID"
            else
                match PrivateInputs.text 4096 secretFile with
                | Error _ -> return Error "OIDC_CREDENTIAL_UNAVAILABLE"
                | Ok secret when String.IsNullOrWhiteSpace secret ->
                    return Error "OIDC_CREDENTIAL_UNAVAILABLE"
                | Ok secret ->
                    use deadline = HttpRequestDeadline.link client cancelled

                    use request =
                        new ClientCredentialsTokenRequest(
                            Address = endpoint.AbsoluteUri,
                            ClientId = clientId,
                            ClientSecret = secret.Trim()
                        )

                    let utc = clock.GetUtcNow()
                    let timestamp = clock.GetTimestamp()

                    try
                        let! response =
                            client.RequestClientCredentialsTokenAsync(request, deadline.Token)

                        return AccessTokens.fromResponseAt clock utc timestamp response
                    with
                    | :? OperationCanceledException
                    | :? HttpRequestException
                    | :? IO.IOException -> return Error "OIDC_TOKEN_UNAVAILABLE"
        }

    let acquire client endpoint clientId secretFile cancelled =
        acquireWithClock TimeProvider.System client endpoint clientId secretFile cancelled
