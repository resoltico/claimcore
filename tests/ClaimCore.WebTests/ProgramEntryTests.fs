module ClaimCore.WebTests.ProgramEntryTests

open Expecto

let private introspectionVerbs () =
    for arguments in
        [
            [| "help" |]
            [| "--help" |]
            [| "version" |]
            [| "--version" |]
            [| "version"; "--json" |]
        ] do
        Expect.equal
            (ClaimCore.Web.Program.main arguments)
            0
            "Configuration-free introspection succeeds without private runtime paths"

    for arguments in [ [| "unsupported" |]; [| "version"; "--yaml" |]; [| "help"; "extra" |] ] do
        Expect.equal
            (ClaimCore.Web.Program.main arguments)
            64
            "Unsupported startup arguments are a usage refusal"

let tests =
    testList
        "Web process entry"
        [
            testCase
                "[CC-WEB-001] startup accepts only configuration-free introspection verbs"
                introspectionVerbs
        ]
