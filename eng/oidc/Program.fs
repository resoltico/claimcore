module ClaimCore.OidcQualification.Program

[<EntryPoint>]
let main _ =
    try
        ClaimCore.WebTests.OidcLiveFixture.run ()
        0
    with error ->
        eprintfn "Synthetic Web OIDC qualification failed: %s." (error.GetType().Name)
        1
