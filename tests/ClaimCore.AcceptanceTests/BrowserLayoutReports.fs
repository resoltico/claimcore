module internal ClaimCore.AcceptanceTests.BrowserLayoutReports

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Security.Cryptography

let private maximumBytes = 16 * 1024

let private tags =
    Set.ofArray (
        "html body div main section aside nav header footer span p a button input select option textarea summary details ul ol li table thead tbody tfoot tr th td dl dt dd h1 h2 h3 h4 h5 h6 svg path use circle line polyline polygon rect text g defs clippath symbol bdi time label form fieldset legend strong small del ins br hr pre code img figure figcaption em b i u sup sub output dialog"
            .Split(' ')
    )

let private exact (names: string list) (value: JsonElement) =
    value.ValueKind = JsonValueKind.Object
    && (value.EnumerateObject() |> Seq.map _.Name |> Seq.sort |> Seq.toList) = List.sort names

let private text (name: string) (pattern: string) (value: JsonElement) =
    let item = value.GetProperty name

    item.ValueKind = JsonValueKind.String
    && Regex.IsMatch(
        item.GetString() |> nonNull,
        pattern,
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )

let private number (name: string) (value: JsonElement) =
    let item = value.GetProperty name

    item.ValueKind = JsonValueKind.Number
    && (match item.TryGetDouble() with
        | true, result -> Double.IsFinite result
        | _ -> false)

let private element (names: string list) (numbers: string list) (value: JsonElement) =
    exact names value
    && text "tag" @"\A[a-z]{1,20}\z" value
    && Set.contains (value.GetProperty("tag").GetString() |> nonNull) tags
    && text "className" @"\A[a-zA-Z0-9_-]{0,40}\z" value
    && List.forall (fun name -> number name value) numbers

let private array (name: string) (cap: int) (check: JsonElement -> bool) (value: JsonElement) =
    let items = value.GetProperty name

    items.ValueKind = JsonValueKind.Array
    && items.GetArrayLength() <= cap
    && (items.EnumerateArray() |> Seq.forall check)

/// Accept only the existing safeReport projection, never arbitrary browser data or private fields.
let validate (bytes: byte array) =
    if bytes.Length > maximumBytes then
        None
    else
        try
            use document = JsonDocument.Parse bytes
            let value = document.RootElement

            if
                exact
                    [
                        "stage"
                        "clientWidth"
                        "scrollWidth"
                        "bodyWidth"
                        "offenders"
                        "scrollable"
                    ]
                    value
                && text "stage" @"\A(login|cases|narrow|zoom)\z" value
                && List.forall
                    (fun name -> number name value)
                    [ "clientWidth"; "scrollWidth"; "bodyWidth" ]
                && array
                    "offenders"
                    5
                    (element
                        [ "tag"; "className"; "left"; "right"; "width" ]
                        [ "left"; "right"; "width" ])
                    value
                && array
                    "scrollable"
                    10
                    (element
                        [ "tag"; "className"; "clientWidth"; "scrollWidth" ]
                        [ "clientWidth"; "scrollWidth" ])
                    value
            then
                Some bytes
            else
                None
        with :? JsonException ->
            None

let retain (output: string) (reportDirectory: string) (engine: string) =
    if Set.contains engine (set [ "chromium"; "firefox"; "webkit" ]) then
        for path in Directory.EnumerateFiles(output, "layout.json", SearchOption.AllDirectories) do
            try
                use stream = File.OpenRead path
                use reader = new BinaryReader(stream)

                match reader.ReadBytes(maximumBytes + 1) |> validate with
                | None -> ()
                | Some bytes ->
                    let relative = Path.GetRelativePath(output, path)

                    let hash =
                        SHA256.HashData(Encoding.UTF8.GetBytes relative) |> Convert.ToHexStringLower

                    let directory = Path.Combine(reportDirectory, "browser-failure")
                    Directory.CreateDirectory directory |> ignore
                    FixtureFiles.write directory ($"{engine}-layout-{hash}.json") bytes |> ignore
            with
            | :? IOException
            | :? UnauthorizedAccessException -> ()
