namespace ClaimCore.Cli

open System
open System.Net.Http
open System.Threading
open Duende.IdentityModel.Client

module OidcClient =
    /// Uses the maintained client's default HTTPS, issuer and endpoint validation policy.
    let discover (client: HttpClient) (issuer: Uri) (cancelled: CancellationToken) =
        task {
            use deadline = HttpRequestDeadline.link client cancelled

            try
                let! response = client.GetDiscoveryDocumentAsync(issuer.AbsoluteUri, deadline.Token)

                if not response.IsError then
                    return Ok response
                elif response.ErrorType = ResponseErrorType.PolicyViolation then
                    return Error "OIDC_METADATA_INVALID"
                else
                    return Error "OIDC_METADATA_UNAVAILABLE"
            with
            | :? HttpRequestException
            | :? OperationCanceledException
            | :? IO.IOException -> return Error "OIDC_METADATA_UNAVAILABLE"
            | :? ArgumentException
            | :? InvalidOperationException -> return Error "OIDC_METADATA_INVALID"
        }
