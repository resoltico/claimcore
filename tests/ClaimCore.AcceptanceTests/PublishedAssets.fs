module internal ClaimCore.AcceptanceTests.PublishedAssets

open System
open System.IO
open System.Text.Json
open Expecto

let private refusesAssets (fixture: PublishedFixture.Fixture) reason =
    let result =
        ProcessRunner.dotnet 60_000 fixture.WebBinary [ "serve" ] fixture.WebEnvironment None

    Expect.equal result.ExitCode 3 reason

    Expect.equal
        (Array.tryLast result.StandardError)
        (Some 10uy)
        "The startup diagnostic is a complete log line"

    use error = JsonDocument.Parse(ReadOnlyMemory result.StandardError)
    Expect.equal (error.RootElement.GetProperty("code").GetString()) "ASSETSMISSING" reason

let qualify (fixture: PublishedFixture.Fixture) =
    let directory = Path.GetDirectoryName fixture.WebBinary |> nonNull
    let index = Path.Combine(directory, "wwwroot/index.html")
    let held = index + ".held"
    File.Move(index, held)

    try
        refusesAssets fixture "Missing published index prevents host opening"
    finally
        File.Move(held, index)

    let viewer = Path.Combine(directory, "wwwroot/attachment-preview.viewer")
    let original = File.ReadAllBytes viewer

    try
        File.AppendAllText(viewer, "\n")
        refusesAssets fixture "A tampered published viewer prevents host opening"
    finally
        File.WriteAllBytes(viewer, original)

    let expected = Path.Combine(fixture.Root, "web/dist")

    for source in Directory.GetFiles(expected, "*", SearchOption.AllDirectories) do
        let relative = Path.GetRelativePath(expected, source)
        let published = Path.Combine(directory, "wwwroot", relative)

        Expect.equal
            (File.ReadAllBytes published)
            (File.ReadAllBytes source)
            ("Published asset bytes: " + relative)
