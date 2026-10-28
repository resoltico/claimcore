module ClaimCore.TestSupport.IdentityRealm

open ClaimCore.Json

open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type Account =
    {
        Subject: string option
        Username: string
    }

type Definition =
    {
        Name: string
        Origin: string
        AccessTokenLifespan: int
        Accounts: Account list
    }

type ClientSecrets =
    {
        Web: string
        Service: string
        Unscoped: string
    }

let ownerSubject = "11111111-1111-4111-8111-111111111111"
let stewardSubject = "22222222-2222-4222-8222-222222222222"
let readerSubject = "33333333-3333-4333-8333-333333333333"
let editorSubject = "44444444-4444-4444-8444-444444444444"

let workflowAccounts =
    [
        ownerSubject, "synthetic-owner"
        stewardSubject, "synthetic-steward"
        readerSubject, "synthetic-reader"
        editorSubject, "synthetic-editor"
        "55555555-5555-4555-8555-555555555555", "synthetic-outsider"
    ]
    |> List.map (fun (subject, username) ->
        {
            Subject = Some subject
            Username = username
        })

let browserAccounts =
    [
        {
            Subject = None
            Username = "synthetic-human"
        }
    ]

let private parse source =
    JsonNode.Parse(source: string) |> nonNull

type private ClientDefinition =
    {
        Id: string
        Secret: string
        Public: bool
        Interactive: bool
        Scoped: bool
        Redirects: string
    }

let private client (definition: ClientDefinition) =
    let id = definition.Id
    let secret = definition.Secret
    let publicClient = definition.Public
    let interactive = definition.Interactive
    let scoped = definition.Scoped
    let redirects = definition.Redirects

    let node =
        parse """{"protocol":"openid-connect","enabled":true,"directAccessGrantsEnabled":false}"""

    node["clientId"] <- JsonValue.Create<string> id
    node["secret"] <- JsonValue.Create<string> secret
    node["publicClient"] <- JsonValue.Create<bool> publicClient
    node["standardFlowEnabled"] <- JsonValue.Create<bool> interactive
    node["serviceAccountsEnabled"] <- JsonValue.Create(not interactive)
    node["redirectUris"] <- parse redirects
    node["attributes"] <- parse """{"pkce.code.challenge.method":"S256"}"""

    if scoped then
        node["protocolMappers"] <-
            parse
                """[{"name":"api-audience","protocol":"openid-connect","protocolMapper":"oidc-audience-mapper","config":{"included.custom.audience":"api","id.token.claim":"false","access.token.claim":"true"}}]"""

    node

let bytes (definition: Definition) (secrets: ClientSecrets) password =
    let realm = parse """{"enabled":true,"sslRequired":"external"}"""
    realm["realm"] <- JsonValue.Create definition.Name
    realm["accessTokenLifespan"] <- JsonValue.Create definition.AccessTokenLifespan
    let clients = JsonArray()

    clients.Add(
        client
            {
                Id = "web"
                Secret = secrets.Web
                Public = false
                Interactive = true
                Scoped = true
                Redirects = JsonSerializer.Serialize [| definition.Origin + "/signin-oidc" |]
            }
    )

    clients.Add(
        client
            {
                Id = "cli"
                Secret = ""
                Public = true
                Interactive = true
                Scoped = true
                Redirects = """["http://127.0.0.1"]"""
            }
    )

    clients.Add(
        client
            {
                Id = "service"
                Secret = secrets.Service
                Public = false
                Interactive = false
                Scoped = true
                Redirects = "[]"
            }
    )

    clients.Add(
        client
            {
                Id = "second-service"
                Secret = secrets.Service
                Public = false
                Interactive = false
                Scoped = true
                Redirects = "[]"
            }
    )

    clients.Add(
        client
            {
                Id = "unscoped"
                Secret = secrets.Unscoped
                Public = false
                Interactive = false
                Scoped = false
                Redirects = "[]"
            }
    )

    realm["clients"] <- clients
    let users = JsonArray()

    for account in definition.Accounts do
        let user =
            parse
                """{"enabled":true,"firstName":"Synthetic","lastName":"User","emailVerified":true,"requiredActions":[]}"""

        account.Subject
        |> Option.iter (fun subject -> user["id"] <- JsonValue.Create subject)

        user["username"] <- JsonValue.Create account.Username
        user["email"] <- JsonValue.Create(account.Username + "@example.test")
        let credential = parse """{"type":"password","temporary":false}"""
        credential["value"] <- JsonValue.Create<string> password
        let credentials = JsonArray()
        credentials.Add credential
        user["credentials"] <- credentials
        users.Add user

    realm["users"] <- users
    realm.ToJsonString() |> Encoding.UTF8.GetBytes
