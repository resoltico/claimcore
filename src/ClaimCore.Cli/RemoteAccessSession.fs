namespace ClaimCore.Cli

open System
open ClaimCore.Domain
open ClaimCore.Protocol
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
                    | None -> return Error AccessFailure.OidcMetadataInvalid
                    | Some address ->
                        return!
                            task {
                                let endpoint = Uri address

                                let! result =
                                    OAuthTokenClient.acquire
                                        client
                                        endpoint
                                        configuration.ClientId
                                        secret
                                        cancelled

                                return
                                    result
                                    |> Result.map (fun access ->
                                        AuthorizationGrant(access, None, endpoint))
                            }
            | _ -> return Error AccessFailure.OidcGrantInvalid
        }

/// Access and refresh tokens remain process-local and are discarded by the shell idle lock.
type RemoteAccessSession
    private (configuration: RemoteConfiguration, oidcTrust: RemoteTls, serviceTrust: RemoteTls) =
    let serviceClient = new HttpClient(serviceTrust.Handler, false)

    let credentials =
        new MemoryTokenSession(
            RemoteTokenRequest.acquire configuration oidcTrust.Handler,
            OAuthRefresh.run TimeProvider.System oidcTrust.Handler configuration.ClientId
        )

    do serviceClient.Timeout <- TimeSpan.FromSeconds 20.

    member _.Configuration = configuration
    member _.Client = serviceClient
    member _.Token(cancelled: CancellationToken) = credentials.Token cancelled
    member internal _.DiscardTokens(cancelled: CancellationToken) = credentials.Discard cancelled

    interface IDisposable with
        member _.Dispose() =
            (credentials :> IDisposable).Dispose()
            serviceClient.Dispose()
            (oidcTrust :> IDisposable).Dispose()
            (serviceTrust :> IDisposable).Dispose()

    static member private ConfigurationFailure setting reason =
        match reason with
        | AccessFailure.TlsTrustRootUnavailable ->
            ClientFailure.ConfigurationIssue(setting, ConfigurationIssue.PrivateInputRefused)
        | AccessFailure.TlsTrustRootInvalid
        | AccessFailure.TlsTrustRootScopeInvalid ->
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
