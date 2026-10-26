namespace ClaimCore.Cli

open System
open ClaimCore.Contract
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
                return Error AccessFailure.OidcTokenEndpointInvalid
            else
                match PrivateInputs.text CliConfigurationLimits.MaximumSecretBytes secretFile with
                | Error _ -> return Error AccessFailure.OidcCredentialUnavailable
                | Ok secret when String.IsNullOrWhiteSpace secret ->
                    return Error AccessFailure.OidcCredentialUnavailable
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
                    | :? IO.IOException -> return Error AccessFailure.OidcTokenUnavailable
        }

    let acquire client endpoint clientId secretFile cancelled =
        acquireWithClock TimeProvider.System client endpoint clientId secretFile cancelled
