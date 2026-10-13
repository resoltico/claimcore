module internal ClaimCore.WebTests.IdentityRealm

open System.Text.Json.Nodes
open System.Text

let private obj (text: string) = JsonNode.Parse text |> nonNull

let private audience () =
    obj
        """{"name":"api-audience","protocol":"openid-connect","protocolMapper":"oidc-audience-mapper","config":{"included.custom.audience":"api","id.token.claim":"false","access.token.claim":"true"}}"""

let private client id secret interactive scoped =
    let node = JsonObject()
    node.Add("clientId", JsonValue.Create<string> id)
    node.Add("secret", JsonValue.Create<string> secret)
    node.Add("protocol", JsonValue.Create "openid-connect")
    node.Add("publicClient", JsonValue.Create false)
    node.Add("standardFlowEnabled", JsonValue.Create<bool> interactive)
    node.Add("serviceAccountsEnabled", JsonValue.Create(not interactive))

    if interactive then
        node.Add("redirectUris", obj """["https://localhost:5443/signin-oidc"]""")

    if scoped then
        let mappings = JsonArray() in
        mappings.Add(audience ())
        node.Add("protocolMappers", mappings)

    node

let bytes realm webSecret serviceSecret unscopedSecret password =
    let root = JsonObject()
    root.Add("realm", JsonValue.Create<string> realm)
    root.Add("enabled", JsonValue.Create true)
    root.Add("sslRequired", JsonValue.Create "external")
    root.Add("accessTokenLifespan", JsonValue.Create 15)
    let clients = JsonArray()
    clients.Add(client "web" webSecret true true)
    clients.Add(client "service" serviceSecret false true)
    clients.Add(client "unscoped" unscopedSecret false false)
    root.Add("clients", clients)

    let user =
        obj
            """{"username":"synthetic-human","firstName":"Synthetic","lastName":"User","email":"synthetic@example.test","emailVerified":true,"enabled":true}"""

    let credential = JsonObject()
    credential.Add("type", JsonValue.Create "password")
    credential.Add("value", JsonValue.Create<string> password)
    credential.Add("temporary", JsonValue.Create false)
    let credentials = JsonArray()
    credentials.Add credential
    user.AsObject().Add("credentials", credentials)
    let users = JsonArray()
    users.Add user
    root.Add("users", users)
    root.ToJsonString() |> Encoding.UTF8.GetBytes
