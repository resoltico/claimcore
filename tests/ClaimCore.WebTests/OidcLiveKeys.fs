module ClaimCore.WebTests.OidcLiveKeys

open System
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks

let private jsonText (root: JsonElement) (name: string) =
    root.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> invalidOp "Synthetic Keycloak admin field is missing.")

let private adminToken origin (client: HttpClient) username password =
    task {
        use form =
            new FormUrlEncodedContent(
                [
                    KeyValuePair("grant_type", "password")
                    KeyValuePair("client_id", "admin-cli")
                    KeyValuePair("username", username)
                    KeyValuePair("password", password)
                ]
            )

        use! response =
            client.PostAsync(origin + "/realms/master/protocol/openid-connect/token", form)

        if int response.StatusCode <> 200 then
            invalidOp "Synthetic Keycloak admin token was refused."

        use! body = JsonDocument.ParseAsync(response.Content.ReadAsStreamAsync().Result)
        return jsonText body.RootElement "access_token"
    }

let private adminGet (client: HttpClient) token (endpoint: string) =
    task {
        use request = new HttpRequestMessage(HttpMethod.Get, endpoint)
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        use! response = client.SendAsync(request)

        if int response.StatusCode <> 200 then
            invalidOp "Synthetic Keycloak admin read failed."

        return! JsonDocument.ParseAsync(response.Content.ReadAsStreamAsync().Result)
    }

let private adminPost (client: HttpClient) token (endpoint: string) payload =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        request.Content <- new StringContent(payload, Encoding.UTF8, "application/json")
        use! response = client.SendAsync(request)

        if int response.StatusCode <> 201 && int response.StatusCode <> 204 then
            invalidOp "Synthetic Keycloak key rotation was refused."
    }

let private realmName (issuer: Uri) =
    issuer.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
    |> Array.last

let private keyId (metadata: JsonDocument) =
    metadata.RootElement.GetProperty("active").GetProperty("RS256").GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> invalidOp "Synthetic Keycloak active RSA key is missing.")

let rotate issuer (client: HttpClient) username password =
    task {
        let authority = Uri issuer
        let origin = authority.GetLeftPart(UriPartial.Authority)
        let realm = realmName authority
        let! token = adminToken origin client username password
        let endpoint = origin + "/admin/realms/" + realm
        use! realmInfo = adminGet client token endpoint
        let realmId = jsonText realmInfo.RootElement "id"
        use! prior = adminGet client token (endpoint + "/keys")
        let before = keyId prior

        let payload =
            JsonSerializer.Serialize(
                {|
                    name = "claimcore-synthetic-rotated"
                    providerId = "rsa-generated"
                    providerType = "org.keycloak.keys.KeyProvider"
                    parentId = realmId
                    config =
                        {|
                            priority = [| "200" |]
                            enabled = [| "true" |]
                            active = [| "true" |]
                            algorithm = [| "RS256" |]
                            keySize = [| "2048" |]
                        |}
                |}
            )

        do! adminPost client token (endpoint + "/components") payload
        use! current = adminGet client token (endpoint + "/keys")

        if keyId current = before then
            invalidOp "Synthetic Keycloak active RSA key did not rotate."
    }
