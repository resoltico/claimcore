module ClaimCore.Tests.DocumentationTests

open System
open System.IO
open System.Text
open System.Xml.Linq
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
            testCase "unnamed contracts and unknown test tokens are rejected" (fun () ->
                let heading = "### " + token + " — Example"
                let name = "test [" + token + "]"

                Expect.isEmpty
                    (DocumentationChecks.contracts [ path, heading ] [ name ])
                    "Named contract"

                Expect.equal
                    (DocumentationChecks.contracts [ path, heading ] []).Length
                    1
                    "Unnamed heading refuses"

                Expect.equal
                    (DocumentationChecks.contracts [] [ name ]).Length
                    1
                    "Unknown token refuses")
            testCase
                "contract names come from discovered cases rather than incidental source tokens"
                (fun () ->
                    let heading = "### " + token + " — Example"
                    let incidental = "// [" + token + "]\nlet unused = \"[" + token + "]\""

                    let output =
                        "Discovered 1 tests in assembly - fixture\n  ordinary test\n" + incidental

                    let names = DocumentationDiscovery.nativeNames output

                    Expect.equal
                        (DocumentationChecks.contracts [ path, heading ] names).Length
                        1
                        "Source comments and unused strings are not discovered names"

                    let actualName = String.concat "" [ "actual test ["; token; "]" ]

                    Expect.isEmpty
                        (DocumentationChecks.contracts [ path, heading ] [ actualName ])
                        "A dynamically formed discovered name owns the contract")
            testCase "a fenced contract heading is not a current contract" (fun () ->
                let fenced = "```markdown\n### " + token + " — Example\n```"

                Expect.isEmpty
                    (DocumentationChecks.contracts [ path, fenced ] [])
                    "Code is not a heading"

                Expect.equal
                    (DocumentationChecks.contracts [ path, fenced ] [ "test [" + token + "]" ])
                        .Length
                    1
                    "A fenced example cannot own a discovered token")
            testCase "native discovery rejects absent empty and mismatched counts" (fun () ->
                for output in
                    [
                        "  a test"
                        "Discovered 0 tests in assembly - fixture"
                        "Discovered 2 tests in assembly - fixture\n  only one"
                        "Discovered 1 tests in assembly - fixture\n  "
                    ] do
                    Expect.throws
                        (fun () -> DocumentationDiscovery.nativeNames output |> ignore)
                        "Incomplete native discovery refuses")
            testCase "frontend discovery rejects empty names and browser import failures" (fun () ->
                let bytes text = Encoding.UTF8.GetBytes(text: string)

                for json in [ "[]"; "[{}]"; "[{\"name\":\"\"}]" ] do
                    Expect.throws
                        (fun () -> DocumentationDiscovery.vitestNames (bytes json) |> ignore)
                        "Invalid frontend discovery refuses"

                for json in
                    [
                        "{\"errors\":[{}],\"suites\":[]}"
                        "{\"errors\":[],\"suites\":[]}"
                        "{\"errors\":[],\"suites\":[{\"title\":\"file\",\"specs\":[{\"title\":\"case\",\"tests\":[]}]}]}"
                    ] do
                    Expect.throws
                        (fun () -> DocumentationDiscovery.playwrightNames (bytes json) |> ignore)
                        "Failed or empty browser discovery refuses"

                Expect.equal
                    (DocumentationDiscovery.vitestNames (bytes "[{\"name\":\"actual case\"}]"))
                    [ "actual case" ]
                    "Actual frontend name")
            testCase "browser discovery retains nested names and every engine case" (fun () ->
                let json =
                    "{\"errors\":[],\"suites\":[{\"title\":\"file\",\"specs\":[],\"suites\":[{\"title\":\"group\",\"specs\":[{\"title\":\"case\",\"tests\":[{\"projectName\":\"chromium\"},{\"projectName\":\"firefox\"}]}]}]}]}"

                Expect.equal
                    (DocumentationDiscovery.playwrightNames (Encoding.UTF8.GetBytes json))
                    [ "file.group.case.chromium"; "file.group.case.firefox" ]
                    "Nested runner names and engine membership")
            testCase
                "solution discovery refuses no tests duplicate projects and failed subprocesses"
                (fun () ->
                    for xml in
                        [
                            "<Solution><Project Path=\"src/Core.fsproj\" /></Solution>"
                            "<Solution><Project Path=\"Tests.fsproj\" /><Project Path=\"Tests.fsproj\" /></Solution>"
                        ] do
                        Expect.throws
                            (fun () ->
                                DocumentationDiscovery.projects "/fixture" (XDocument.Parse xml)
                                |> ignore)
                            "Absent or duplicate test membership refuses"

                    let failed: ClaimCore.TestSupport.BoundedProcessResult =
                        {
                            ExitCode = 1
                            StandardOutput = [||]
                            StandardError = [||]
                        }

                    Expect.throws
                        (fun () ->
                            DocumentationDiscovery.successfulOutput "fixture" failed |> ignore)
                        "A failed discovery command cannot contribute evidence")
            testCase "oversized frontend discovery evidence is rejected" (fun () ->
                let file = Path.GetTempFileName()

                try
                    use stream = File.OpenWrite file
                    stream.SetLength(1_048_577L)
                    stream.Dispose()

                    Expect.throws
                        (fun () -> DocumentationDiscovery.reportBytes file |> ignore)
                        "Oversized discovery evidence refuses"
                finally
                    File.Delete file)
        ]
