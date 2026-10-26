module ClaimCore.Tests.DocumentationTests

open System
open System.IO
open Expecto

let private token = "CC-" + "EXAMPLE-999"
let private path = Path.GetFullPath("documentation-control.md")

[<Tests>]
let tests =
    testList
        "documentation"
        [
            testCase "current relative links and contract ownership are complete" (fun () ->
                let failures =
                    DocumentationChecks.repository (ClaimCore.TestSupport.RepositoryRoot.find ())

                Expect.isEmpty failures (String.concat "\n" failures))
            testCase "a missing relative file is rejected" (fun () ->
                let failures =
                    DocumentationChecks.links
                        (Map.ofList [ path, "[missing](absent.md)" ])
                        (fun _ -> false)

                Expect.equal failures.Length 1 "Missing-file negative control")
            testCase "a missing relative heading is rejected" (fun () ->
                let failures =
                    DocumentationChecks.links
                        (Map.ofList [ path, "# Present\n[missing](#absent)" ])
                        (fun _ -> true)

                Expect.equal failures.Length 1 "Missing-anchor negative control"

                Expect.isEmpty
                    (DocumentationChecks.links
                        (Map.ofList [ path, "# Present\n[valid](#present)" ])
                        (fun _ -> true))
                    "Existing anchor"

                Expect.isEmpty
                    (DocumentationChecks.links
                        (Map.ofList [ path, "<a id=\"explicit\"></a>\n[valid](#explicit)" ])
                        (fun _ -> true))
                    "Explicit HTML anchor"

                Expect.equal
                    (DocumentationChecks.links
                        (Map.ofList
                            [ path, "```html\n<a id=\"example\"></a>\n```\n[missing](#example)" ])
                        (fun _ -> true))
                        .Length
                    1
                    "A code example is not an anchor")
            testCase "contract headings require valid unique owner tokens" (fun () ->
                let heading = "### " + token + " — Example"

                Expect.isEmpty
                    (DocumentationChecks.contracts [ path, heading ])
                    "Valid owner token"

                Expect.equal
                    (DocumentationChecks.contracts [ path, heading; path + "-other", heading ])
                        .Length
                    1
                    "Duplicate refuses"

                for value in
                    [
                        "### CC-EXAMPLE-1 — Bad"
                        "### CC-example-999 — Bad"
                        "### CC-EXAMPLE-9999 — Bad"
                    ] do
                    Expect.equal
                        (DocumentationChecks.contracts [ path, value ]).Length
                        1
                        "Malformed refuses"

                Expect.isEmpty
                    (DocumentationChecks.contracts [ path, "```markdown\n" + heading + "\n```" ])
                    "Fenced example ignored")
        ]
