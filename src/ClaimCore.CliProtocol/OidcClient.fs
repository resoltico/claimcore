namespace ClaimCore.Cli

open System
open System.Security.Cryptography
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading

[<NoComparison>]
type OidcEndpoints =
    {
        Authorization: Uri
        Token: Uri
        Jwks: Uri
    }

module OidcClient =
    let private tryHttps (value: string) =
        let mutable uri = Unchecked.defaultof<Uri>

        if
            Uri.TryCreate(value, UriKind.Absolute, &uri)
            && uri.Scheme = Uri.UriSchemeHttps
            && not (String.IsNullOrWhiteSpace(uri.Host))
            && String.IsNullOrEmpty(uri.UserInfo)
            && String.IsNullOrEmpty(uri.Query)
            && String.IsNullOrEmpty(uri.Fragment)
        then
            Some uri
        else
            None

    let private textProperty (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if root.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then
            value.GetString() |> Option.ofObj
        else
            None

    /// Metadata is accepted only for the configured issuer and HTTPS endpoints. Nothing supplied
    /// by a provider is copied into a diagnostic or process output on refusal.
    let parseDiscovery (issuer: Uri) (bytes: byte array) =
        if bytes.Length > 65536 then
            Error "OIDC_METADATA_INVALID"
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory bytes)
                let root = document.RootElement

                let names =
                    if root.ValueKind = JsonValueKind.Object then
                        root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                    else
                        []

                if
                    root.ValueKind <> JsonValueKind.Object
                    || names.Length <> (names |> Set.ofList |> Set.count)
                then
                    Error "OIDC_METADATA_INVALID"
                else
                    match
                        textProperty "issuer" root,
                        textProperty "authorization_endpoint" root |> Option.bind tryHttps,
                        textProperty "token_endpoint" root |> Option.bind tryHttps,
                        textProperty "jwks_uri" root |> Option.bind tryHttps
                    with
                    | Some actualIssuer, Some authorization, Some token, Some jwks when
                        actualIssuer = issuer.AbsoluteUri.TrimEnd('/')
                        ->
                        Ok
                            {
                                Authorization = authorization
                                Token = token
                                Jwks = jwks
                            }
                    | _ -> Error "OIDC_METADATA_INVALID"
            with :? JsonException ->
                Error "OIDC_METADATA_INVALID"

    let discover (client: HttpClient) (issuer: Uri) (cancelled: CancellationToken) =
        task {
            let location =
                Uri(issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration")

            use request = new HttpRequestMessage(HttpMethod.Get, location)

            try
                use! response =
                    client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancelled)

                if not response.IsSuccessStatusCode then
                    return Error "OIDC_METADATA_UNAVAILABLE"
                else
                    do! response.Content.LoadIntoBufferAsync(65536L)
                    let! bytes = response.Content.ReadAsByteArrayAsync(cancelled)
                    return parseDiscovery issuer bytes
            with
            | :? HttpRequestException
            | :? OperationCanceledException
            | :? IO.IOException
            | :? InvalidOperationException -> return Error "OIDC_METADATA_UNAVAILABLE"
        }

    let private base64Url (bytes: byte array) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    type PkceSession =
        {
            Verifier: string
            Challenge: string
            State: string
            Nonce: string
        }

    let createPkceSession () =
        let random () =
            RandomNumberGenerator.GetBytes(32) |> base64Url

        let verifier = random ()

        {
            Verifier = verifier
            Challenge = SHA256.HashData(Encoding.ASCII.GetBytes(verifier)) |> base64Url
            State = random ()
            Nonce = random ()
        }

    let stateMatches (expected: string) (actual: string) =
        let left = Encoding.ASCII.GetBytes(expected)
        let right = Encoding.ASCII.GetBytes(actual)

        left.Length = right.Length
        && CryptographicOperations.FixedTimeEquals(left, right)

    let authorizationUri endpoints clientId (redirect: Uri) (session: PkceSession) =
        if
            not redirect.IsLoopback
            || redirect.Scheme <> Uri.UriSchemeHttp
            || redirect.AbsolutePath <> "/"
            || not (String.IsNullOrEmpty(redirect.Query))
            || not (String.IsNullOrEmpty(redirect.Fragment))
        then
            Error "OIDC_REDIRECT_INVALID"
        else
            let values =
                [
                    "response_type", "code"
                    "client_id", clientId
                    "redirect_uri", redirect.GetLeftPart(UriPartial.Authority)
                    "scope", "openid"
                    "state", session.State
                    "nonce", session.Nonce
                    "code_challenge", session.Challenge
                    "code_challenge_method", "S256"
                ]

            let query =
                values
                |> List.map (fun (key, value) ->
                    Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value))
                |> String.concat "&"

            let builder = UriBuilder(endpoints.Authorization)
            builder.Query <- query
            Ok builder.Uri
