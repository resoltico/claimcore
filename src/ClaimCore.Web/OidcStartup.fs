namespace ClaimCore.Web

open System
open System.Net.Http
open System.Threading
open ClaimCore.Contracts

module internal OidcStartup =
    let verify (client: HttpClient) (configuration: OidcConfiguration) =
        use deadline = new CancellationTokenSource(client.Timeout)

        try
            use response =
                client
                    .GetAsync(
                        OidcAuthority.discoveryLocation configuration.Issuer,
                        HttpCompletionOption.ResponseHeadersRead,
                        deadline.Token
                    )
                    .GetAwaiter()
                    .GetResult()

            if not response.IsSuccessStatusCode then
                WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid

            response.Content.LoadIntoBufferAsync(65536L, deadline.Token).GetAwaiter().GetResult()

            let bytes =
                response.Content.ReadAsByteArrayAsync(deadline.Token).GetAwaiter().GetResult()

            match OidcAuthority.validateMetadata configuration.Issuer (ReadOnlyMemory bytes) with
            | Ok() -> ()
            | Error _ -> WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid
        with
        | :? HttpRequestException
        | :? OperationCanceledException
        | :? InvalidOperationException ->
            WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid
