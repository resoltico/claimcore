namespace ClaimCore.Cli

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ClaimCore.HostSecurity

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
    let lifetime = TimeSpan.FromSeconds(float expiresInSeconds - 30.)
    let expiresAt = observedUtc.Add(lifetime)
    member _.Authorization = AuthenticationHeaderValue("Bearer", value)
    member _.ExpiresAt = expiresAt

    member _.CanReuse =
        let elapsed = clock.GetElapsedTime(observedTimestamp)
        elapsed >= TimeSpan.Zero && elapsed < lifetime && clock.GetUtcNow() < expiresAt

    override _.ToString() = "<redacted access token>"

[<RequireQualifiedAccess; NoComparison>]
type OAuthGrant =
    | AuthorizationCode of code: string * redirect: Uri * verifier: string
    | ClientCredentials of privateSecretFile: string

module OAuthTokenClient =
    let private tokenText (root: JsonElement) =
        let mutable token = Unchecked.defaultof<JsonElement>

        if
            root.TryGetProperty("access_token", &token)
            && token.ValueKind = JsonValueKind.String
        then
            token.GetString() |> Option.ofObj
        else
            None

    let private bearerType (root: JsonElement) =
        let mutable kind = Unchecked.defaultof<JsonElement>

        root.TryGetProperty("token_type", &kind)
        && kind.ValueKind = JsonValueKind.String
        && kind.GetString() = "Bearer"

    let private positiveExpiry (root: JsonElement) =
        let mutable expiry = Unchecked.defaultof<JsonElement>
        let mutable seconds = 0

        root.TryGetProperty("expires_in", &expiry)
        && expiry.ValueKind = JsonValueKind.Number
        && expiry.TryGetInt32(&seconds)
        && seconds > 0

    let private readLimited maximum (response: HttpResponseMessage) token =
        task {
            do! response.Content.LoadIntoBufferAsync(int64 maximum, token)
            let! bytes = response.Content.ReadAsByteArrayAsync(token)

            return
                if bytes.Length <= maximum then
                    Ok bytes
                else
                    Error "OIDC_RESPONSE_INVALID"
        }

    let internal parseTokenResponseAt
        (clock: TimeProvider)
        observedUtc
        observedTimestamp
        (bytes: byte array)
        =
        try
            use document = JsonDocument.Parse(ReadOnlyMemory bytes)
            let root = document.RootElement

            let unique =
                if root.ValueKind = JsonValueKind.Object then
                    let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                    names.Length = (names |> Set.ofList |> Set.count)
                else
                    false

            let value = if unique then tokenText root else None

            match value with
            | Some value when
                bearerType root
                && positiveExpiry root
                && value.Length >= 1
                && value.Length <= 8192
                ->
                Ok(
                    AccessToken(
                        value,
                        root.GetProperty("expires_in").GetInt32(),
                        clock,
                        observedUtc,
                        observedTimestamp
                    )
                )
            | _ -> Error "OIDC_RESPONSE_INVALID"
        with :? JsonException ->
            Error "OIDC_RESPONSE_INVALID"

    let parseTokenResponse bytes =
        let clock = TimeProvider.System
        parseTokenResponseAt clock (clock.GetUtcNow()) (clock.GetTimestamp()) bytes

    let private grantFields clientId =
        function
        | OAuthGrant.AuthorizationCode(code, redirect, verifier) when
            redirect.IsLoopback
            && redirect.Scheme = Uri.UriSchemeHttp
            && redirect.AbsolutePath = "/"
            && not (String.IsNullOrWhiteSpace(code))
            && not (String.IsNullOrWhiteSpace(verifier))
            ->
            Ok
                [
                    "grant_type", "authorization_code"
                    "client_id", clientId
                    "code", code
                    "redirect_uri", redirect.GetLeftPart(UriPartial.Authority)
                    "code_verifier", verifier
                ]
        | OAuthGrant.ClientCredentials path ->
            match PrivateFileService.readUtf8Text 4096 path with
            | Ok secret when not (String.IsNullOrWhiteSpace(secret)) ->
                Ok
                    [
                        "grant_type", "client_credentials"
                        "client_id", clientId
                        "client_secret", secret.Trim()
                    ]
            | _ -> Error "OIDC_CREDENTIAL_UNAVAILABLE"
        | _ -> Error "OIDC_GRANT_INVALID"

    let private deliveredToken clock observedUtc observedTimestamp bytes =
        match parseTokenResponseAt clock observedUtc observedTimestamp bytes with
        | Ok token when token.CanReuse -> Ok token
        | Ok _ -> Error "OIDC_TOKEN_UNAVAILABLE"
        | Error reason -> Error reason

    let private acquireHttps
        (clock: TimeProvider)
        (client: HttpClient)
        endpoints
        clientId
        grant
        (cancelled: CancellationToken)
        =
        task {
            use deadline = HttpRequestDeadline.link client cancelled
            let cancelled = deadline.Token

            match grantFields clientId grant with
            | Error reason -> return Error reason
            | Ok fields ->
                use request = new HttpRequestMessage(HttpMethod.Post, endpoints.Token)

                request.Content <-
                    new FormUrlEncodedContent(
                        fields |> Seq.map (fun (key, value) -> KeyValuePair(key, value))
                    )

                request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))

                let observedUtc = clock.GetUtcNow()
                let observedTimestamp = clock.GetTimestamp()

                try
                    use! response =
                        client.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancelled
                        )

                    if response.StatusCode <> HttpStatusCode.OK then
                        return Error "OIDC_TOKEN_UNAVAILABLE"
                    else
                        match! readLimited 32768 response cancelled with
                        | Error reason -> return Error reason
                        | Ok bytes ->
                            match
                                parseTokenResponseAt clock observedUtc observedTimestamp bytes
                            with
                            | Ok token when token.CanReuse -> return Ok token
                            | Ok _ -> return Error "OIDC_TOKEN_UNAVAILABLE"
                            | Error reason -> return Error reason
                with
                | :? OperationCanceledException -> return Error "OIDC_TOKEN_UNAVAILABLE"
                | :? HttpRequestException -> return Error "OIDC_TOKEN_UNAVAILABLE"
                | :? IO.IOException -> return Error "OIDC_TOKEN_UNAVAILABLE"
        }

    /// The supplied transport must disable redirects and enforce TLS. Provider details and response
    /// bodies are never returned on refusal.
    let internal acquireWithClock
        clock
        (client: HttpClient)
        endpoints
        clientId
        grant
        (cancelled: CancellationToken)
        =
        task {
            if endpoints.Token.Scheme <> Uri.UriSchemeHttps then
                return Error "OIDC_TOKEN_ENDPOINT_INVALID"
            else
                return! acquireHttps clock client endpoints clientId grant cancelled
        }

    let acquire client endpoints clientId grant cancelled =
        acquireWithClock TimeProvider.System client endpoints clientId grant cancelled
