namespace ClaimCore.Cli

open System
open ClaimCore.Catalog
open System.Net.Http
open System.Threading

module private RemoteTokenRequest =
    let acquire (configuration: RemoteConfiguration) (handler: HttpMessageHandler) cancelled =
        task {
            match configuration.Mode, configuration.SecretFile with
            | RemoteClientMode.Interactive, None ->
                return!
                    InteractiveAuthorization.obtain
                        handler
                        configuration.Issuer
                        configuration.ClientId
                        cancelled
            | RemoteClientMode.Automation, Some secret ->
                use client = new HttpClient(handler, false)
                client.Timeout <- TimeSpan.FromSeconds 20.
                client.MaxResponseContentBufferSize <- 65536L

                match! OidcClient.discover client configuration.Issuer cancelled with
                | Error reason -> return Error reason
                | Ok metadata ->
                    match Option.ofObj metadata.TokenEndpoint with
                    | None -> return Error "OIDC_METADATA_INVALID"
                    | Some address ->
                        return!
                            OAuthTokenClient.acquire
                                client
                                (Uri address)
                                configuration.ClientId
                                secret
                                cancelled
            | _ -> return Error "OIDC_GRANT_INVALID"
        }

/// Tokens remain process-local and are reacquired before expiry. No refresh token is persisted.
type RemoteAccessSession
    private (configuration: RemoteConfiguration, oidcTrust: RemoteTls, serviceTrust: RemoteTls) =
    let serviceClient = new HttpClient(serviceTrust.Handler, false)
    let gate = new SemaphoreSlim(1, 1)
    let mutable current: AccessToken option = None

    do serviceClient.Timeout <- TimeSpan.FromSeconds(20.)

    member _.Configuration = configuration
    member _.Client = serviceClient

    member _.Token(cancelled: CancellationToken) =
        task {
            do! gate.WaitAsync(cancelled)

            try
                match current with
                | Some token when token.CanReuse -> return Ok token
                | _ ->
                    match! RemoteTokenRequest.acquire configuration oidcTrust.Handler cancelled with
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
            serviceClient.Dispose()
            (oidcTrust :> IDisposable).Dispose()
            (serviceTrust :> IDisposable).Dispose()

    static member private ConfigurationFailure setting reason =
        match reason with
        | "TLS_TRUST_ROOT_UNAVAILABLE" ->
            ClientFailure.ConfigurationIssue(setting, ConfigurationIssue.PrivateInputRefused)
        | "TLS_TRUST_ROOT_INVALID"
        | "TLS_TRUST_ROOT_SCOPE_INVALID" ->
            ClientFailure.ConfigurationIssue(setting, ConfigurationIssue.Invalid)
        | _ -> ClientFailure.Configuration

    static member Open(configuration: RemoteConfiguration) =
        match RemoteTls.Create(configuration.OidcTrustRootFile, configuration.Issuer) with
        | Error reason ->
            Error(RemoteAccessSession.ConfigurationFailure CliSetting.OidcTrustRoot reason)
        | Ok oidcTrust ->
            match RemoteTls.Create(configuration.ServiceTrustRootFile, configuration.Service) with
            | Error reason ->
                (oidcTrust :> IDisposable).Dispose()
                Error(RemoteAccessSession.ConfigurationFailure CliSetting.ServiceTrustRoot reason)
            | Ok serviceTrust -> Ok(new RemoteAccessSession(configuration, oidcTrust, serviceTrust))
