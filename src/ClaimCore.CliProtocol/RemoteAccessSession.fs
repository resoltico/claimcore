namespace ClaimCore.Cli

open System
open System.Net.Http
open System.Threading

module private RemoteTokenRequest =
    let private grant configuration endpoints cancelled =
        task {
            match configuration.Mode, configuration.SecretFile with
            | RemoteClientMode.Automation, Some path -> return Ok(OAuthGrant.ClientCredentials path)
            | RemoteClientMode.Interactive, None ->
                match!
                    InteractiveAuthorization.obtain
                        endpoints
                        configuration.Issuer
                        configuration.ClientId
                        cancelled
                with
                | Ok(code, redirect, verifier) ->
                    return Ok(OAuthGrant.AuthorizationCode(code, redirect, verifier))
                | Error reason -> return Error reason
            | _ -> return Error "OIDC_GRANT_INVALID"
        }

    let acquire (configuration: RemoteConfiguration) (client: HttpClient) cancelled =
        task {
            match! OidcClient.discover client configuration.Issuer cancelled with
            | Error reason -> return Error reason
            | Ok endpoints ->
                match! grant configuration endpoints cancelled with
                | Error reason -> return Error reason
                | Ok request ->
                    return!
                        OAuthTokenClient.acquire
                            client
                            endpoints
                            configuration.ClientId
                            request
                            cancelled
        }

/// Tokens remain process-local and are reacquired before expiry. No refresh token is persisted.
type RemoteAccessSession
    private (configuration: RemoteConfiguration, oidcTrust: RemoteTls, serviceTrust: RemoteTls) =
    let oidcClient = new HttpClient(oidcTrust.Handler, false)
    let serviceClient = new HttpClient(serviceTrust.Handler, false)
    let gate = new SemaphoreSlim(1, 1)
    let mutable current: AccessToken option = None

    do
        oidcClient.Timeout <- TimeSpan.FromSeconds(20.)
        serviceClient.Timeout <- TimeSpan.FromSeconds(20.)

    member _.Configuration = configuration
    member _.Client = serviceClient

    member _.Token(cancelled: CancellationToken) =
        task {
            do! gate.WaitAsync(cancelled)

            try
                match current with
                | Some token when token.ExpiresAt > DateTimeOffset.UtcNow -> return Ok token
                | _ ->
                    match! RemoteTokenRequest.acquire configuration oidcClient cancelled with
                    | Error reason -> return Error reason
                    | Ok token ->
                        current <- Some token
                        return Ok token
            finally
                gate.Release() |> ignore
        }

    interface IDisposable with
        member _.Dispose() =
            gate.Dispose()
            oidcClient.Dispose()
            serviceClient.Dispose()
            (oidcTrust :> IDisposable).Dispose()
            (serviceTrust :> IDisposable).Dispose()

    static member Open(configuration: RemoteConfiguration) =
        match RemoteTls.Create(configuration.OidcTrustRootFile, configuration.Issuer) with
        | Error reason -> Error reason
        | Ok oidcTrust ->
            match RemoteTls.Create(configuration.ServiceTrustRootFile, configuration.Service) with
            | Error reason ->
                (oidcTrust :> IDisposable).Dispose()
                Error reason
            | Ok serviceTrust -> Ok(new RemoteAccessSession(configuration, oidcTrust, serviceTrust))
