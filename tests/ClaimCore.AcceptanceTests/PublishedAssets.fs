module internal ClaimCore.AcceptanceTests.PublishedAssets

open System
open System.IO
open System.Text.Json
open Expecto

let qualify (fixture: PublishedFixture.Fixture) =
    let directory = Path.GetDirectoryName fixture.WebBinary |> nonNull
    let index = Path.Combine(directory, "wwwroot/index.html")
    let held = index + ".held"
    File.Move(index, held)

    try
        let result =
            ProcessRunner.dotnet 60_000 fixture.WebBinary [] fixture.WebEnvironment None

        Expect.equal result.ExitCode 3 "Missing published index prevents host opening"
        use error = JsonDocument.Parse(ReadOnlyMemory result.StandardError)

        Expect.equal
            (error.RootElement.GetProperty("code").GetString())
            "ASSETSMISSING"
            "The asset guard rejects the absent index"
    finally
        File.Move(held, index)

    let expected = Path.Combine(fixture.Root, "web/dist")

    for source in Directory.GetFiles(expected, "*", SearchOption.AllDirectories) do
        let relative = Path.GetRelativePath(expected, source)
        let published = Path.Combine(directory, "wwwroot", relative)

        Expect.equal
            (File.ReadAllBytes published)
            (File.ReadAllBytes source)
            ("Published asset bytes: " + relative)
