namespace ClaimCore.Web

open System
open System.Net.Http
open System.Threading
open ClaimCore.Contracts

module internal OidcStartup =
    let verify (client: HttpClient) (configuration: OidcConfiguration) (ct: CancellationToken) =
        task {
            use deadline = CancellationTokenSource.CreateLinkedTokenSource(ct)
            deadline.CancelAfter(client.Timeout)

            try
                use! response =
                    client.GetAsync(
                        OidcAuthority.discoveryLocation configuration.Issuer,
                        HttpCompletionOption.ResponseHeadersRead,
                        deadline.Token
                    )

                if not response.IsSuccessStatusCode then
                    WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid

                do! response.Content.LoadIntoBufferAsync(65536L, deadline.Token)
                let! bytes = response.Content.ReadAsByteArrayAsync(deadline.Token)

                match
                    OidcAuthority.validateMetadata configuration.Issuer (ReadOnlyMemory bytes)
                with
                | Ok() -> ()
                | Error _ -> WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                return raise (OperationCanceledException(ct))
            | :? HttpRequestException
            | :? OperationCanceledException
            | :? InvalidOperationException ->
                return WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid
        }
