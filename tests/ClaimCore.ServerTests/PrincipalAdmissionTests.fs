module ClaimCore.ServerTests.PrincipalAdmissionTests

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain

open System
open System.Text
open System.Security.Claims
open Expecto
open ClaimCore.Server

let private issuer = Uri("https://issuer.example.test/realm")

let private principal claims =
    ClaimsPrincipal(ClaimsIdentity(claims, "verified-bearer"))

let private bearer claims =
    let client =
        claims
        |> List.tryFind (fun (claim: Claim) -> claim.Type = "azp")
        |> Option.map _.Value

    let profile = if client = Some "cli" then "human" else "service"

    let evidence =
        [
            Claim("typ", "Bearer")
            Claim("claimcore_principal", profile)
            Claim("iat", "1800000000", ClaimValueTypes.Integer64)
            Claim("auth_time", "1800000000", ClaimValueTypes.Integer64)
        ]

    PrincipalIdentity.fromAccessToken
        issuer
        "cli"
        (set [ "service-a"; "service-b" ])
        (principal (claims @ evidence))

let private ambiguousClaims () =
    for first, second in [ "cli", "service"; "service", "cli"; "cli", "cli" ] do
        Expect.isError
            (bearer [ Claim("azp", first); Claim("azp", second); Claim("sub", "human") ])
            "Repeated client identity never chooses the first people"

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
        (bearer exact |> Result.map AuthenticatedPrincipals.key)
        (PrincipalKey.human issuer.AbsoluteUri "human"
         |> Result.mapError (fun _ -> AuthenticationFailure.PrincipalInvalid))
        "Distinct extensions cannot replace exact identity claims"

let private clientClaims () =
    for claims in [ [ Claim("azp", "cli"); Claim("client_id", "cli"); Claim("sub", "human") ] ] do
        Expect.equal
            (bearer claims |> Result.map AuthenticatedPrincipals.key)
            (PrincipalIdentity.human issuer "human" |> Result.map AuthenticatedPrincipals.key)
            "Matching client claims select the CLI human"

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
        (bearer [ Claim("azp", "service-a") ] |> Result.map AuthenticatedPrincipals.key)
        (PrincipalIdentity.service issuer "service-a"
         |> Result.map AuthenticatedPrincipals.key)
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
        (PrincipalIdentity.fromAccessToken issuer "cli" (Set.singleton "service-a") combined)
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
        "\"issuer\":\"https://issuer.example.test/realm\",\"authorization_endpoint\":\"https://issuer.example.test/auth\",\"token_endpoint\":\"https://issuer.example.test/token\",\"end_session_endpoint\":\"https://issuer.example.test/logout\",\"jwks_uri\":\"https://issuer.example.test/keys\""

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
        let people =
            OidcAuthority.parse source
            |> Result.defaultWith (fun _ -> failtest "A canonical HTTPS issuer is valid.")

        let metadata =
            $"""{{"issuer":"{source}","authorization_endpoint":"https://issuer.example.test/auth/","token_endpoint":"https://issuer.example.test/token/","end_session_endpoint":"https://issuer.example.test/logout","jwks_uri":"https://issuer.example.test/keys/"}}"""

        let validate value =
            OidcAuthority.validateMetadata
                people
                (ReadOnlyMemory(Encoding.UTF8.GetBytes(value: string)))

        Expect.isOk (validate metadata) "Slash-ended HTTPS endpoints do not change issuer identity"

        Expect.isError
            (validate (metadata.Replace(source, source.TrimEnd('/'))))
            "Issuer slash substitution is refused"

let private boundedHints () =
    let admit extra =
        bearer ([ Claim("azp", "cli"); Claim("sub", "human") ] @ extra)
        |> Result.defaultWith (fun error ->
            failtestf "Identity remains valid independently of optional hints: %A" error)

    let text = String.replicate 200 "😀"

    let accepted =
        admit
            [
                Claim("name", text)
                Claim("preferred_username", "user")
                Claim("email", "hint@example.invalid")
            ]

    let hints = AuthenticatedPrincipals.claims accepted

    Expect.equal
        (hints.Name |> Option.map PartyName.format)
        (Some text)
        "Hint cap counts Unicode scalars"

    Expect.equal
        (hints.PreferredUsername |> Option.map PartyName.format)
        (Some "user")
        "Exact authenticated hint keys are retained"

    for bad in [ text + "😀"; "first\nsecond"; "" ] do
        let value = admit [ Claim("name", bad) ]

        Expect.isNone
            (AuthenticatedPrincipals.claims value).Name
            "Invalid optional hints cannot enter context or cache"

        Expect.equal
            (AuthenticatedPrincipals.key value)
            (AuthenticatedPrincipals.key accepted)
            "Hint admission does not alter authenticated identity"

    let duplicates = admit [ Claim("name", "first"); Claim("name", "second") ]

    Expect.isNone
        (AuthenticatedPrincipals.claims duplicates).Name
        "Ambiguous optional hints are omitted, never first-wins"

    let browser =
        PrincipalIdentity.fromBrowserSession
            issuer
            (principal [ Claim("sub", "human"); Claim("name", "Browser hint") ])
        |> Result.defaultWith (string >> failtest)

    Expect.equal
        ((AuthenticatedPrincipals.claims browser).Name |> Option.map PartyName.format)
        (Some "Browser hint")
        "Verified browser-session hints follow the same admission"

let private providerProof () =
    let issued = DateTimeOffset.FromUnixTimeSeconds 1800000020L

    let signed purpose profile client issuedType =
        principal
            [
                Claim("typ", purpose)
                Claim("claimcore_principal", profile)
                Claim("azp", client)
                Claim("sub", "human")
                Claim("iat", "1800000020", issuedType)
                Claim("auth_time", "1800000010", ClaimValueTypes.Integer64)
            ]

    let classify =
        PrincipalIdentity.fromAccessToken issuer "cli" (Set.singleton "service-a")

    Expect.isOk
        (classify (signed "Bearer" "human" "cli" ClaimValueTypes.Integer64))
        "Pinned human profile"

    Expect.isOk
        (classify (signed "Bearer" "service" "service-a" ClaimValueTypes.Integer64))
        "Pinned service profile"

    for fixture in
        [
            signed "ID" "human" "cli" ClaimValueTypes.Integer64
            signed "Bearer" "human" "web" ClaimValueTypes.Integer64
            signed "Bearer" "service" "cli" ClaimValueTypes.Integer64
            signed "Bearer" "service" "unrelated" ClaimValueTypes.Integer64
            signed "Bearer" "human" "cli" ClaimValueTypes.String
        ] do
        Expect.isError
            (classify fixture)
            "Purpose, configured client, kind and proof are all required"

    let cut = DateTimeOffset.FromUnixTimeSeconds 1800000010L
    let before = cut.AddSeconds(-1.)
    let after = cut.AddSeconds(3.)

    let floor observed stamp =
        ClaimCore.Application.CredentialFreshness.after observed cut stamp

    Expect.isFalse (floor issued cut) "Equality and same-second issuance fail closed"
    Expect.isFalse (floor issued (cut.AddSeconds 2.)) "The maximum possible lead is ambiguous"
    Expect.isFalse (floor issued (issued.AddSeconds 3.)) "An implausible future issuer time fails"
    Expect.isFalse (floor before after) "A database clock before the cut fails"
    Expect.isTrue (floor issued after) "Post-cut machine issuance with enough separation qualifies"

    let proof auth : ClaimCore.Application.HumanAuthentication =
        {
            IssuedAt = issued
            AuthenticatedAt = auth
        }

    let human auth =
        ClaimCore.Application.CredentialFreshness.human issued cut (proof auth)

    Expect.isFalse (human before) "Refresh with an earlier human authentication fails"
    Expect.isTrue (human after) "Both human evidence fields are post-cut"

[<Tests>]
let tests =
    testList
        "exact principal admission"
        [
            testCase "token profiles and bounded credential freshness fail closed" providerProof
            testCase
                "authenticated optional hints have scalar caps and never change identity"
                boundedHints
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
