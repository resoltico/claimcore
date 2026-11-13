module ClaimCore.Tests.DocumentationTests

open System
open System.IO
open Expecto

let private path = Path.GetFullPath("documentation-control.md")

[<Tests>]
let tests =
    testList
        "documentation"
        [
            testCase "current relative files and anchors resolve" (fun () ->
                let failures =
                    DocumentationChecks.repository (ClaimCore.TestSupport.RepositoryRoot.find ())

                Expect.isEmpty failures (String.concat "\n" failures))
            testCase "standing contribution terms and pull request consent are explicit" (fun () ->
                let root = ClaimCore.TestSupport.RepositoryRoot.find ()
                let notice = File.ReadAllText(Path.Combine(root, "PROJECT-NOTICE.md"))
                let sections = notice.Split("\n## Contribution terms\n", StringSplitOptions.None)
                Expect.equal sections.Length 2 "One contribution-terms heading owns the anchor"

                let expected =
                    """By submitting a contribution (code, documentation, tests or other material) to this
repository, you:

- confirm that you wrote it or otherwise have the right to submit it on these terms;
- license it to everyone under this repository's public license, `AGPL-3.0-only`; and
- also grant Ervins Strauhmanis a perpetual, worldwide, non-exclusive, royalty-free,
  irrevocable license to use, reproduce, modify, distribute and sublicense it, and to license
  it under other terms, including the commercial terms described in
  [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md).

You keep the copyright in your contribution. Do not submit material you cannot license on
these terms; third-party components keep their own licenses. Each pull request confirms these
terms with the checkbox in its template."""

                let normalize (value: string) =
                    System.Text.RegularExpressions.Regex.Replace(value, "\\s+", " ").Trim()

                Expect.equal
                    (normalize sections[1])
                    (normalize expected)
                    "The standing grants and contributor assurances permit line wrapping only"

                let template =
                    File.ReadAllText(Path.Combine(root, ".github", "PULL_REQUEST_TEMPLATE.md"))

                Expect.stringContains template "\n## Contribution terms\n" "Consent section"

                Expect.stringContains
                    template
                    "- [ ] I agree to the [contribution terms](https://github.com/resoltico/claimcore/blob/main/PROJECT-NOTICE.md#contribution-terms) for this contribution."
                    "Each pull request offers explicit consent with an absolute anchor link")
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
        ]
