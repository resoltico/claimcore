namespace ClaimCore.Cli

open System
open System.Net.Http.Headers
open Duende.IdentityModel.Client

/// A token is kept in memory only. Its textual representation is deliberately non-secret.
type AccessToken
    internal
    (
        value: string,
        expiresInSeconds: int,
        clock: TimeProvider,
        observedUtc: DateTimeOffset,
        observedTimestamp: int64
    ) =
    let lifetime = TimeSpan.FromSeconds(float expiresInSeconds)
    let reuseLifetime = lifetime - TimeSpan.FromSeconds 30.
    let expiresAt = observedUtc.Add(reuseLifetime)

    let within budget =
        let elapsed = clock.GetElapsedTime(observedTimestamp)

        elapsed >= TimeSpan.Zero
        && elapsed < budget
        && clock.GetUtcNow() < observedUtc.Add(budget)

    member _.Authorization = AuthenticationHeaderValue("Bearer", value)
    member _.ExpiresAt = expiresAt

    member _.CanUse = within lifetime
    member _.CanReuse = within reuseLifetime

    override _.ToString() = "<redacted access token>"

module AccessTokens =
    let internal fromPartsAt
        clock
        observedUtc
        observedTimestamp
        (tokenType: string | null)
        (value: string | null)
        seconds
        =
        match value with
        | null -> Error "OIDC_RESPONSE_INVALID"
        | value ->
            try
                if
                    value.Length < 1
                    || value.Length > 8192
                    || seconds <= 0
                    || not (String.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
                    || not (
                        value
                        |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || "-._~+/=".Contains c)
                    )
                then
                    Error "OIDC_RESPONSE_INVALID"
                else
                    let token = AccessToken(value, seconds, clock, observedUtc, observedTimestamp)

                    if token.CanUse then
                        Ok token
                    else
                        Error "OIDC_TOKEN_UNAVAILABLE"
            with :? ArgumentException ->
                Error "OIDC_RESPONSE_INVALID"

    let internal fromResponseAt clock observedUtc observedTimestamp (response: TokenResponse) =
        if response.IsError then
            Error "OIDC_TOKEN_UNAVAILABLE"
        elif
            response.Raw
            |> Option.ofObj
            |> Option.forall (fun raw -> System.Text.Encoding.UTF8.GetByteCount raw > 32768)
        then
            Error "OIDC_RESPONSE_INVALID"
        else
            try
                fromPartsAt
                    clock
                    observedUtc
                    observedTimestamp
                    response.TokenType
                    response.AccessToken
                    response.ExpiresIn
            with
            | :? System.Text.Json.JsonException
            | :? InvalidOperationException
            | :? FormatException
            | :? OverflowException
            | :? ArgumentException -> Error "OIDC_RESPONSE_INVALID"
