module ClaimCore.Tests.ConfigurationTests

open System
open System.IO
open System.Text.Json
open Expecto

let private root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

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
                Expect.equal
                    (composeImage ())
                    (baselineImage ())
                    "No mutable or duplicated image selection")
        ]
