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
type AccessToken internal (value: string, expiresInSeconds: int) =
    let expiresAt = DateTimeOffset.UtcNow.AddSeconds(float expiresInSeconds - 30.)
    member _.Authorization = AuthenticationHeaderValue("Bearer", value)
    member _.ExpiresAt = expiresAt
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
            use! source = response.Content.ReadAsStreamAsync(token)
            let buffer = Array.zeroCreate<byte>(maximum + 1)
            let mutable count = 0
            let mutable reading = true

            while reading && count < buffer.Length do
                let! received = source.ReadAsync(buffer.AsMemory(count), token)

                if received = 0 then
                    reading <- false
                else
                    count <- count + received

            if count > maximum then
                return Error "OIDC_RESPONSE_INVALID"
            else
                return Ok(Array.take count buffer)
        }

    let parseTokenResponse (bytes: byte array) =
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
                Ok(AccessToken(value, root.GetProperty("expires_in").GetInt32()))
            | _ -> Error "OIDC_RESPONSE_INVALID"
        with :? JsonException ->
            Error "OIDC_RESPONSE_INVALID"

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

    let private acquireHttps
        (client: HttpClient)
        endpoints
        clientId
        grant
        (cancelled: CancellationToken)
        =
        task {
            match grantFields clientId grant with
            | Error reason -> return Error reason
            | Ok fields ->
                use request = new HttpRequestMessage(HttpMethod.Post, endpoints.Token)

                request.Content <-
                    new FormUrlEncodedContent(
                        fields |> Seq.map (fun (key, value) -> KeyValuePair(key, value))
                    )

                request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))

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
                        | Ok bytes -> return parseTokenResponse bytes
                with
                | :? OperationCanceledException -> return Error "OIDC_TOKEN_UNAVAILABLE"
                | :? HttpRequestException -> return Error "OIDC_TOKEN_UNAVAILABLE"
                | :? IO.IOException -> return Error "OIDC_TOKEN_UNAVAILABLE"
        }

    /// The supplied transport must disable redirects and enforce TLS. Provider details and response
    /// bodies are never returned on refusal.
    let acquire (client: HttpClient) endpoints clientId grant (cancelled: CancellationToken) =
        task {
            if endpoints.Token.Scheme <> Uri.UriSchemeHttps then
                return Error "OIDC_TOKEN_ENDPOINT_INVALID"
            else
                return! acquireHttps client endpoints clientId grant cancelled
        }
