module internal ClaimCore.AcceptanceTests.IdentityRealm

open System.Text.Json.Nodes
open System.Text

let ownerSubject = "11111111-1111-4111-8111-111111111111"
let stewardSubject = "22222222-2222-4222-8222-222222222222"
let readerSubject = "33333333-3333-4333-8333-333333333333"
let editorSubject = "44444444-4444-4444-8444-444444444444"

let users =
    [
        ownerSubject, "synthetic-owner"
        stewardSubject, "synthetic-steward"
        readerSubject, "synthetic-reader"
        editorSubject, "synthetic-editor"
        "55555555-5555-4555-8555-555555555555", "synthetic-outsider"
    ]

let private parse source =
    JsonNode.Parse(source: string) |> nonNull

let private client id secret publicClient interactive redirects =
    let node =
        parse """{"protocol":"openid-connect","enabled":true,"directAccessGrantsEnabled":false}"""

    node["clientId"] <- JsonValue.Create<string> id
    node["secret"] <- JsonValue.Create<string> secret
    node["publicClient"] <- JsonValue.Create<bool> publicClient
    node["standardFlowEnabled"] <- JsonValue.Create<bool> interactive
    node["serviceAccountsEnabled"] <- JsonValue.Create(not interactive)
    node["redirectUris"] <- parse redirects
    node["attributes"] <- parse """{"pkce.code.challenge.method":"S256"}"""

    node["protocolMappers"] <-
        parse
            """[{"name":"api-audience","protocol":"openid-connect","protocolMapper":"oidc-audience-mapper","config":{"included.custom.audience":"api","id.token.claim":"false","access.token.claim":"true"}}]"""

    node

let bytes origin webSecret serviceSecret password =
    let realm =
        parse
            """{"realm":"synthetic","enabled":true,"sslRequired":"external","accessTokenLifespan":300}"""

    let clients = JsonArray()

    clients.Add(
        client
            "web"
            webSecret
            false
            true
            (System.Text.Json.JsonSerializer.Serialize [| origin + "/signin-oidc" |])
    )

    clients.Add(client "cli" "" true true """["http://127.0.0.1"]""")
    clients.Add(client "service" serviceSecret false false "[]")
    realm["clients"] <- clients
    let entries = JsonArray()

    for subject, username in users do
        let user =
            parse
                """{"enabled":true,"firstName":"Synthetic","lastName":"User","emailVerified":true,"requiredActions":[]}"""

        user["id"] <- JsonValue.Create<string> subject
        user["username"] <- JsonValue.Create<string> username
        user["email"] <- JsonValue.Create(username + "@example.test")
        let credential = JsonObject()
        credential["type"] <- JsonValue.Create "password"
        credential["value"] <- JsonValue.Create<string> password
        credential["temporary"] <- JsonValue.Create false
        let credentials = JsonArray()
        credentials.Add credential
        user["credentials"] <- credentials
        entries.Add user

    realm["users"] <- entries
    realm.ToJsonString() |> Encoding.UTF8.GetBytes
