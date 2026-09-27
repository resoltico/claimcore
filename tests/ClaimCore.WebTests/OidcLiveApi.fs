module ClaimCore.WebTests.OidcLiveApi

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open ClaimCore.WebTests.OidcLiveBrowser
open ClaimCore.WebTests.OidcLiveKeys
open ClaimCore.WebTests.OidcLiveTokens

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

let private caseRequest (local: HttpClient) token browserCookies =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/cases/get")

        request.Content <-
            new StringContent(
                """{"caseReference":"SYNTHETIC-001"}""",
                Encoding.UTF8,
                "application/json"
            )

        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token)
        |> ignore

        browserCookies
        |> Option.iter (fun values ->
            request.Headers.TryAddWithoutValidation("Cookie", String.concat "; " values)
            |> ignore)

        use! response = local.SendAsync(request)
        return int response.StatusCode
    }

let private requireDenied (local: HttpClient) tokens =
    task {
        for token in tokens do
            let! status = bearerRequest local token

            if status <> 401 then
                invalidOp "Synthetic invalid bearer was accepted."
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
        let! caseAllowed = caseRequest local token None
        let! mixed = caseRequest local token (Some browserCookies)

        if allowed <> 204 || caseAllowed <> 200 || mixed <> 401 then
            invalidOp "Synthetic case API credential dispatch failed."

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

        do! Task.Delay(TimeSpan.FromSeconds(17.))
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
