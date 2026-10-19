module ClaimCore.WebTests.OidcLiveApi

open ClaimCore.Wire

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open ClaimCore.WebTests.OidcLiveBrowser
open ClaimCore.WebTests.OidcLiveKeys
open ClaimCore.WebTests.OidcLiveTokens

exception CredentialDispatchMismatch of int * int * int
exception InvalidBearerAdmission of int * int

let private value (root: JsonElement) (name: string) =
    root.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> invalidOp "Synthetic credential field is missing.")

let private serviceToken issuer (identity: HttpClient) clientId clientSecret =
    task {
        use form =
            new FormUrlEncodedContent(
                [
                    KeyValuePair("grant_type", "client_credentials")
                    KeyValuePair("client_id", clientId)
                    KeyValuePair("client_secret", clientSecret)
                ]
            )

        use! response = identity.PostAsync(issuer + "/protocol/openid-connect/token", form)
        requireStatus 200 response
        use! body = JsonDocument.ParseAsync(response.Content.ReadAsStreamAsync().Result)
        return value body.RootElement "access_token"
    }

let bearerRequest (local: HttpClient) token =
    task {
        use request = new HttpRequestMessage(HttpMethod.Get, "/auth/bearer")

        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token)
        |> ignore

        use! response = local.SendAsync(request)
        return int response.StatusCode
    }

let private caseRequest (local: HttpClient) token browserCookies reference =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, "/api/case.get")

        request.Content <-
            new StringContent(
                JsonSerializer.Serialize {| caseReference = reference |},
                Encoding.UTF8,
                "application/json"
            )

        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token)
        |> ignore

        request.Headers.TryAddWithoutValidation("ClaimCore-Contract", Fingerprint.value)
        |> ignore

        browserCookies
        |> Option.iter (fun values ->
            request.Headers.TryAddWithoutValidation("Cookie", String.concat "; " values)
            |> ignore)

        use! response = local.SendAsync(request)
        let! payload = response.Content.ReadAsByteArrayAsync()
        use document = JsonDocument.Parse payload

        let outcome =
            match document.RootElement.TryGetProperty "outcome" with
            | true, value -> Some(value.Clone())
            | _ -> None

        return int response.StatusCode, outcome
    }

let private requireDenied (local: HttpClient) tokens =
    task {
        for index, token in List.indexed tokens do
            let! status = bearerRequest local token

            if status <> 401 then
                raise (InvalidBearerAdmission(index, status))
    }

let private wrongIssuerToken issuer (identity: HttpClient) (credentials: JsonElement) =
    let authority = Uri issuer

    let foreignIssuer =
        authority.GetLeftPart(UriPartial.Authority)
        + "/realms/"
        + value credentials "foreignRealm"

    serviceToken
        foreignIssuer
        identity
        (value credentials "serviceClientId")
        (value credentials "foreignClientSecret")

let private serviceAuthority issuer identity local (credentials: JsonElement) token first =
    task {
        let tag (value: JsonElement option) =
            value |> Option.map (fun value -> value.GetProperty("tag").GetString())

        if tag first <> Some "SUCCEEDED" then
            invalidOp "Registered service could not read its case."

        let! second =
            serviceToken
                issuer
                identity
                (value credentials "secondServiceClientId")
                (value credentials "serviceClientSecret")

        let! authenticated = bearerRequest local second
        let! status, denied = caseRequest local second None "SYNTHETIC-001"
        let! _, absent = caseRequest local token None "SYNTHETIC-MISSING"

        if authenticated <> 204 || status <> 200 || tag denied <> Some "REJECTED" then
            invalidOp "Distinct unregistered service must authenticate and receive a typed refusal."

        let refusal value =
            value
            |> Option.map (fun (value: JsonElement) -> value.GetProperty("data").GetRawText())

        if refusal denied <> refusal absent then
            invalidOp "Absent and inaccessible resources must share the same public refusal."
    }

let qualifyBearers
    issuer
    (identity: HttpClient)
    (local: HttpClient)
    (credentials: JsonElement)
    browserCookies
    =
    task {
        let! token =
            serviceToken
                issuer
                identity
                (value credentials "serviceClientId")
                (value credentials "serviceClientSecret")

        let! allowed = bearerRequest local token
        let! caseAllowed, first = caseRequest local token None "SYNTHETIC-001"
        let! mixed, _ = caseRequest local token (Some browserCookies) "SYNTHETIC-001"

        if allowed <> 204 || caseAllowed <> 200 || mixed <> 401 then
            raise (CredentialDispatchMismatch(allowed, caseAllowed, mixed))

        do! serviceAuthority issuer identity local credentials token first

        let! unscoped =
            serviceToken
                issuer
                identity
                (value credentials "unscopedClientId")
                (value credentials "unscopedClientSecret")

        let! foreign = wrongIssuerToken issuer identity credentials

        do!
            requireDenied
                local
                [
                    token + "x"
                    unscoped
                    foreign
                    alterHeader "none" "JWT" token
                    alterHeader "RS256" "ID" token
                ]

        // The synthetic host has zero JWT clock skew; prior boundary checks consume part of the TTL.
        do! Task.Delay(expiryDelay DateTimeOffset.UtcNow token)
        let! expired = bearerRequest local token

        if expired <> 401 then
            invalidOp "Synthetic expired token was accepted."
    }

let rec private acceptRotated (local: HttpClient) token remaining =
    task {
        let! status = bearerRequest local token

        if status = 204 then
            return ()
        elif remaining = 0 then
            invalidOp "Synthetic rotated signing key was not accepted."
        else
            do! Task.Delay(TimeSpan.FromSeconds(1.))
            return! acceptRotated local token (remaining - 1)
    }

let qualifyRotation issuer (identity: HttpClient) (local: HttpClient) (credentials: JsonElement) =
    task {
        do!
            rotate
                issuer
                identity
                (value credentials "adminUsername")
                (value credentials "adminPassword")

        let! rotated =
            serviceToken
                issuer
                identity
                (value credentials "serviceClientId")
                (value credentials "serviceClientSecret")

        do! acceptRotated local rotated 20
    }
