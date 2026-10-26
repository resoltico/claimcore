module ClaimCore.WebTests.PrincipalAdmissionTests

open ClaimCore.Contract
open ClaimCore.Domain

open System
open System.Text
open System.Security.Claims
open Expecto
open ClaimCore.Web

let private issuer = Uri("https://issuer.example.test/realm")

let private principal claims =
    ClaimsPrincipal(ClaimsIdentity(claims, "verified-bearer"))

let private bearer claims =
    PrincipalIdentity.fromAccessToken issuer "cli" (principal claims)

let private ambiguousClaims () =
    for first, second in [ "cli", "service"; "service", "cli"; "cli", "cli" ] do
        Expect.isError
            (bearer [ Claim("azp", first); Claim("azp", second); Claim("sub", "human") ])
            "Repeated client identity never chooses the first authority"

    for first, second in [ "alice", "bob"; "bob", "alice"; "alice", "alice" ] do
        let claims = [ Claim("azp", "cli"); Claim("sub", first); Claim("sub", second) ]
        Expect.isError (bearer claims) "Repeated subject cannot select an actor"

        Expect.isError
            (PrincipalIdentity.fromBrowserSession issuer (principal claims))
            "Cookie and OIDC admission share exact subject selection"

let private exactClaimNames () =
    Expect.isError
        (bearer [ Claim("AZP", "cli"); Claim("sub", "human") ])
        "Uppercase client name cannot select a client"

    Expect.isError
        (bearer [ Claim("azp", "cli"); Claim("SUB", "human") ])
        "Uppercase subject name cannot select a human"

    Expect.isError
        (PrincipalIdentity.fromBrowserSession issuer (principal [ Claim("SUB", "human") ]))
        "Browser subject selection uses the exact name"

    let exact =
        [
            Claim("azp", "cli")
            Claim("sub", "human")
            Claim("SUB", "extension")
            Claim("AZP", "service")
        ]

    Expect.equal
        (bearer exact)
        (PrincipalIdentity.human issuer "human")
        "Distinct extensions cannot replace exact identity claims"

let private clientClaims () =
    for claims in
        [
            [ Claim("client_id", "cli"); Claim("sub", "human") ]
            [ Claim("azp", "cli"); Claim("client_id", "cli"); Claim("sub", "human") ]
        ] do
        Expect.equal
            (bearer claims)
            (PrincipalIdentity.human issuer "human")
            "Fallback and matching client claims select the CLI human"

    for claims in
        [
            [ Claim("azp", "cli"); Claim("client_id", "service"); Claim("sub", "human") ]
            [ Claim("client_id", "service"); Claim("client_id", "service") ]
            [ Claim("azp", "service"); Claim("client_id", "") ]
            [ Claim("azp", " ") ]
            [ Claim("sub", "human") ]
        ] do
        Expect.isError (bearer claims) "Conflicting duplicate blank and absent clients refuse"

    Expect.equal
        (bearer [ Claim("client_id", "service-a") ])
        (PrincipalIdentity.service issuer "service-a")
        "Client credentials need no human subject"

    Expect.notEqual
        (bearer [ Claim("azp", "service-a") ])
        (bearer [ Claim("azp", "service-b") ])
        "Distinct service clients remain distinct"

    Expect.equal
        (bearer [ Claim("azp", "service-a"); Claim("sub", "one") ])
        (bearer [ Claim("azp", "service-a"); Claim("sub", "two") ])
        "Service identity follows client rather than subject"

let private multipleIdentities () =
    let claims = [ Claim("azp", "cli"); Claim("sub", "human") ]

    let identities =
        [ ClaimsIdentity(claims, "verified"); ClaimsIdentity([], "second") ]

    let combined = ClaimsPrincipal(identities)

    Expect.isError
        (PrincipalIdentity.fromAccessToken issuer "cli" combined)
        "A composite principal has no single actor identity"

    Expect.isError
        (PrincipalIdentity.fromBrowserSession issuer combined)
        "Browser identity cannot silently select a component"

    Expect.isError
        (PrincipalIdentity.fromBrowserSession issuer (ClaimsPrincipal(ClaimsIdentity(claims))))
        "An unauthenticated subject is not a session"

let private metadataShapes () =
    let validate (source: string) =
        OidcAuthority.validateMetadata issuer (ReadOnlyMemory(Encoding.UTF8.GetBytes(source)))

    for source in [ "[]"; "null"; "42"; "\"text\""; "{" ] do
        Expect.isError
            (validate source)
            "Malformed and nonobject discovery refuses without throwing"

    let fields =
        "\"issuer\":\"https://issuer.example.test/realm\",\"authorization_endpoint\":\"https://issuer.example.test/auth\",\"token_endpoint\":\"https://issuer.example.test/token\",\"jwks_uri\":\"https://issuer.example.test/keys\""

    Expect.isOk
        (validate ("{" + fields + ",\"extension\":true}"))
        "Unique provider extensions are allowed"

    for duplicate in
        [
            "\"issuer\":\"https://issuer.example.test/realm\""
            "\"token_endpoint\":\"https://foreign.example.test/token\""
            "\"extension\":false,\"extension\":true"
        ] do
        Expect.isError
            (validate ("{" + fields + "," + duplicate + "}"))
            "Ambiguous discovery is rejected"

let private rootIssuerDiscovery () =
    for source in [ "https://issuer.example.test/"; "https://issuer.example.test/realm/" ] do
        let authority =
            OidcAuthority.parse source
            |> Result.defaultWith (fun _ -> failtest "A canonical HTTPS issuer is valid.")

        let metadata =
            $"""{{"issuer":"{source}","authorization_endpoint":"https://issuer.example.test/auth/","token_endpoint":"https://issuer.example.test/token/","jwks_uri":"https://issuer.example.test/keys/"}}"""

        let validate value =
            OidcAuthority.validateMetadata
                authority
                (ReadOnlyMemory(Encoding.UTF8.GetBytes(value: string)))

        Expect.isOk (validate metadata) "Slash-ended HTTPS endpoints do not change issuer identity"

        Expect.isError
            (validate (metadata.Replace(source, source.TrimEnd('/'))))
            "Issuer slash substitution is refused"

[<Tests>]
let tests =
    testList
        "exact principal admission"
        [
            testCase
                "verified client claims agree exactly and distinct services keep distinct identities"
                clientClaims
            testCase "claim names select identity with ordinal case sensitivity" exactClaimNames
            testCase "repeated subject and client claims are refused in every order" ambiguousClaims
            testCase
                "composite and unauthenticated principals cannot select an actor"
                multipleIdentities
            testCase
                "root and slash-ended issuer metadata preserve exact identity"
                rootIssuerDiscovery
            testCase "discovery refuses nonobjects and duplicate members" metadataShapes
        ]
