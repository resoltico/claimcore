namespace ClaimCore.Cli

open System
open System.Net.Http
open System.Threading
open Duende.IdentityModel.Client

module internal OAuthRefresh =
    let run
        (clock: TimeProvider)
        (handler: HttpMessageHandler)
        clientId
        (grant: AuthorizationGrant)
        (cancelled: CancellationToken)
        =
        task {
            match grant.Refresh with
            | None -> return Error AccessFailure.OidcGrantInvalid
            | Some _ when grant.Endpoint.Scheme <> Uri.UriSchemeHttps ->
                return Error AccessFailure.OidcTokenEndpointInvalid
            | Some refresh ->
                use transport = new OidcBackchannel(handler, (fun () -> Some grant.Endpoint), clock)
                use client = new HttpClient(transport, false)
                client.Timeout <- TimeSpan.FromSeconds 20.
                client.MaxResponseContentBufferSize <- int64 AccessTokens.MaximumResponseBytes
                use deadline = HttpRequestDeadline.link client cancelled

                use request =
                    new RefreshTokenRequest(
                        Address = grant.Endpoint.AbsoluteUri,
                        ClientId = clientId,
                        ClientCredentialStyle = ClientCredentialStyle.PostBody,
                        RefreshToken = refresh.Value
                    )

                try
                    let! response = client.RequestRefreshTokenAsync(request, deadline.Token)

                    match transport.Dispatch with
                    | None -> return Error AccessFailure.OidcTokenUnavailable
                    | Some dispatch ->
                        return
                            AuthorizationGrant.fromResponse
                                {
                                    Clock = clock
                                    Utc = dispatch.Utc
                                    Timestamp = dispatch.Timestamp
                                }
                                grant.Endpoint
                                grant.Refresh
                                response
                with
                | :? OperationCanceledException
                | :? HttpRequestException
                | :? IO.IOException -> return Error AccessFailure.OidcTokenUnavailable
        }
