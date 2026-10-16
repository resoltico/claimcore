module internal ClaimCore.WebTests.IdentityReadiness

open ClaimCore.Web

let wait client issuer token =
    ClaimCore.TestSupport.IdentityReadiness.wait
        client
        (OidcAuthority.discoveryLocation issuer)
        (fun bytes -> OidcAuthority.validateMetadata issuer bytes |> Result.isOk)
        token
