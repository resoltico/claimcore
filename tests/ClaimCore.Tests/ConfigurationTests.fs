module ClaimCore.Tests.ConfigurationTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.TestSupport

let private root = RepositoryRoot.find ()

let private baselineImage () =
    use document =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "db/postgresql-baseline.json")))

    document.RootElement.GetProperty("containerImage").GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "The baseline containerImage must be text.")

let private composeImage () =
    File.ReadLines(Path.Combine(root, "compose.yaml"))
    |> Seq.filter (fun line -> line.TrimStart().StartsWith("image:", StringComparison.Ordinal))
    |> Seq.exactlyOne
    |> fun line ->
        line[(line.IndexOf("image:", StringComparison.Ordinal) + "image:".Length) ..].Trim()

let tests =
    testList
        "configuration projections"
        [
            testCase "Compose uses the canonical digest-pinned PostgreSQL image" (fun () ->
                let decoy =
                    Path.Combine(
                        Path.GetTempPath(),
                        "claimcore-source-link-decoy-" + Guid.NewGuid().ToString("N")
                    )

                Expect.isNone
                    (RepositoryRoot.tryFindFrom decoy)
                    "A source-path-like location without repository markers is not a root"

                Expect.equal
                    (RepositoryRoot.tryFindFrom AppContext.BaseDirectory)
                    (Some root)
                    "The test binary locates the checkout without embedded source paths"

                Expect.equal
                    (composeImage ())
                    (baselineImage ())
                    "No mutable or duplicated image selection")
        ]
