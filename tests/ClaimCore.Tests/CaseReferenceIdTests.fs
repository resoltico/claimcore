module ClaimCore.Tests.CaseReferenceIdTests

open System
open Expecto
open ClaimCore.RecordFormat

let private installation = Guid.Parse "90000000-0000-4000-8000-000000000001"

[<Tests>]
let tests =
    testList
        "case reference identity"
        [
            testCase
                "independent SHA-256 reference vectors match network-order identifiers"
                (fun () ->
                    for reference, expected in
                        [
                            "synthetic-a", "bb9c58de-d6f8-846c-c184-7bf45ebe85ad"
                            "REF-\uFFFD", "c4fe70ed-1b70-f905-5741-4967ecb00ff5"
                            "REF-\U00010000", "485d89f0-fb99-f4d6-eee8-6724e183180f"
                        ] do
                        Expect.equal
                            (CaseReferenceId.encode installation reference)
                            (Guid.Parse expected)
                            "Independent Python oracle")
            testCase "installation and exact scalar text bind the identifier" (fun () ->
                let actual = CaseReferenceId.encode installation "reference"

                for otherInstallation, reference in
                    [
                        Guid.Parse "90000000-0000-4000-8000-000000000002", "reference"
                        installation, "Reference"
                        installation, "reference "
                        installation, "réference"
                        installation, "re\u0301ference"
                    ] do
                    Expect.notEqual
                        (CaseReferenceId.encode otherInstallation reference)
                        actual
                        "No case folding or normalization"

                Expect.notEqual
                    (CaseReferenceId.encode installation "réference")
                    (CaseReferenceId.encode installation "re\u0301ference")
                    "Canonically equivalent scalar sequences remain distinct")
            testCase "empty identities and malformed scalar text are refused" (fun () ->
                Expect.throws
                    (fun () -> CaseReferenceId.encode Guid.Empty "reference" |> ignore)
                    "Installation identity required"

                for reference in [ Unchecked.defaultof<string>; ""; String([| char 0xD800 |]) ] do
                    Expect.throws
                        (fun () -> CaseReferenceId.encode installation reference |> ignore)
                        "Well-formed nonempty reference required")
        ]
