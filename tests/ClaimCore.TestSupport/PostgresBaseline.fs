module ClaimCore.TestSupport.PostgresBaseline

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

[<NoEquality; NoComparison>]
type SupportedMajor =
    {
        Major: int
        MinimumMinor: int
        ContainerImage: string
    }

let private readMajor (element: JsonElement) =
    let major = element.GetProperty("major").GetInt32()
    let minor = element.GetProperty("minimumMinor").GetInt32()
    let image = element.GetProperty("containerImage").GetString() |> nonNull

    let matchImage =
        Regex.Match(image, @"^postgres:([1-9][0-9]*)\.([0-9]+)@sha256:[0-9a-f]{64}$")

    if
        major < 1
        || minor < 0
        || not matchImage.Success
        || matchImage.Groups[1].Value <> string major
        || Int32.Parse(matchImage.Groups[2].Value, Globalization.CultureInfo.InvariantCulture) <
            minor
    then
        invalidOp "PostgreSQL test baseline requires a matching pinned major and minimum minor."

    {
        Major = major
        MinimumMinor = minor
        ContainerImage = image
    }

let parse text =
    use document = JsonDocument.Parse(text: string)

    let entries =
        document.RootElement.GetProperty("supportedMajors").EnumerateArray()
        |> Seq.map readMajor
        |> Seq.toList

    if
        List.isEmpty entries
        || (entries |> List.distinctBy _.Major |> List.length) <> entries.Length
    then
        invalidOp "PostgreSQL test baseline requires a nonempty list of distinct majors."

    entries

let select entries requestedMajor =
    match entries, requestedMajor with
    | first :: _, None -> first
    | _, Some requested ->
        entries
        |> List.tryFind (fun entry -> string entry.Major = requested)
        |> Option.defaultWith (fun () ->
            invalidOp "PostgreSQL test major is not listed in the baseline.")
    | [], None -> invalidOp "PostgreSQL test baseline is empty."

let image () =
    let root = RepositoryRoot.find ()

    let entries =
        File.ReadAllText(Path.Combine(root, "db/postgresql-baseline.json")) |> parse

    let requested =
        Environment.GetEnvironmentVariable("CLAIMCORE_TEST_POSTGRES_MAJOR")
        |> Option.ofObj

    (select entries requested).ContainerImage
