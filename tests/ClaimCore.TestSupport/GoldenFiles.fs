module ClaimCore.TestSupport.GoldenFiles

open System
open System.IO

let private protectedFiles =
    Set.ofList
        [
            "record-format-vectors.json"
            "transition-matrix.tsv"
            "client-byte-conformance.json"
        ]

/// Admission stays independent of inventory contents so a mistaken declaration cannot unlock vectors.
let admit declared filename update ci =
    if not update then
        Ok false
    elif ci then
        Error "Golden updates are refused when CI is set."
    elif Path.GetFileName(filename: string) <> filename || filename.Contains('\\') then
        Error "A golden must be a fixture basename."
    elif Set.contains filename protectedFiles then
        Error "Permanent fixtures cannot be updated."
    elif not (Set.contains filename declared) then
        Error "The fixture is not a declared golden."
    else
        Ok true

let update filename (bytes: byte array) =
    let enabled = Environment.GetEnvironmentVariable "CLAIMCORE_UPDATE_GOLDENS" = "1"
    let ci = not (isNull (Environment.GetEnvironmentVariable "CI"))
    let declared = Inventory.tokens "goldens" |> Set.ofList

    match admit declared filename enabled ci with
    | Error reason -> invalidOp reason
    | Ok false -> ()
    | Ok true ->
        let path = Path.Combine(RepositoryRoot.find (), "tests/fixtures", filename)

        if File.ReadAllBytes path <> bytes then
            File.WriteAllBytes(path, bytes)
