namespace ClaimCore.Cli

open System
open Duende.IdentityModel.Client

/// Refresh material has no printable representation and never leaves the process except to its issuer.
type internal RefreshToken private (value: string) =
    member _.Value = value
    override _.ToString() = "<redacted refresh token>"

    static member Admit(value: string | null) =
        match value with
        | null -> Ok None
        | value when
            value.Length > 0
            && value.Length <= 8192
            && (value |> Seq.forall (fun character -> character >= '!' && character <= '~'))
            ->
            Ok(Some(RefreshToken value))
        | _ -> Error AccessFailure.OidcResponseInvalid

/// The endpoint comes from the validated discovery document used for this grant.
type internal AuthorizationGrant(access: AccessToken, refresh: RefreshToken option, endpoint: Uri) =
    member _.Access = access
    member _.Refresh = refresh
    member _.Endpoint = endpoint
    override _.ToString() = "<redacted authorization grant>"

module internal AuthorizationGrant =
    let fromResponse
        (observation: AccessTokens.Observation)
        endpoint
        previous
        (response: TokenResponse)
        =
        AccessTokens.fromResponseAt observation.Clock observation.Utc observation.Timestamp response
        |> Result.bind (fun access ->
            try
                RefreshToken.Admit response.RefreshToken
                |> Result.map (fun refresh ->
                    AuthorizationGrant(access, refresh |> Option.orElse previous, endpoint))
            with
            | :? System.Text.Json.JsonException
            | :? InvalidOperationException
            | :? FormatException
            | :? OverflowException
            | :? ArgumentException -> Error AccessFailure.OidcResponseInvalid)
