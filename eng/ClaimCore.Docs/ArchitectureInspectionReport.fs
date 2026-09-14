namespace ClaimCore.Docs

open System
open System.IO
open System.Text.Json

/// Independent evidence validation for the bounded ArchUnitNET model snapshot.
[<RequireQualifiedAccess>]
module ArchitectureInspectionReport =
    let fileName = "architecture-report.json"
    let private maximumBytes = 16 * 1024

    let private requiredAssemblies =
        set
            [
                "ClaimCore.Application"
                "ClaimCore.Cli"
                "ClaimCore.Contracts"
                "ClaimCore.Database"
                "ClaimCore.Domain"
                "ClaimCore.HostSecurity"
                "ClaimCore.Postgres"
                "ClaimCore.RecordFormat"
                "ClaimCore.Web"
            ]

    let private exactProperties expected (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error "Architecture report contains a non-object entry."
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList

            if
                names.Length <> (names |> Set.ofList |> Set.count)
                || Set.ofList names <> expected
            then
                Error "Architecture report has missing, extra, or duplicate properties."
            else
                Ok()

    let private text (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)

        if value.ValueKind <> JsonValueKind.String then
            Error "Architecture report has a non-text property."
        else
            match value.GetString() |> Option.ofObj with
            | Some content -> Ok content
            | None -> Error "Architecture report has a null text property."

    let private integer (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)
        let mutable parsed = 0

        if value.ValueKind = JsonValueKind.Number && value.TryGetInt32(&parsed) then
            Ok parsed
        else
            Error "Architecture report has an invalid integer."

    let private array (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)

        if value.ValueKind = JsonValueKind.Array then
            Ok(value.EnumerateArray() |> Seq.toList)
        else
            Error "Architecture report has an invalid array."

    let private traverse parse values =
        values
        |> List.fold
            (fun state item ->
                match state, parse item with
                | Ok entries, Ok value -> Ok(value :: entries)
                | Error failure, _ -> Error failure
                | _, Error failure -> Error failure)
            (Ok [])
        |> Result.map List.rev

    let private assembly (element: JsonElement) =
        exactProperties (set [ "name"; "inspectedTypes" ]) element
        |> Result.bind (fun () ->
            match text "name" element, integer "inspectedTypes" element with
            | Ok name, Ok count when count > 0 -> Ok name
            | _ -> Error "Architecture report has an invalid inspected assembly.")

    let private edge (element: JsonElement) =
        exactProperties (set [ "source"; "target" ]) element
        |> Result.bind (fun () ->
            match text "source" element, text "target" element with
            | Ok source, Ok target when
                source <> target
                && requiredAssemblies.Contains source
                && requiredAssemblies.Contains target
                ->
                Ok(source, target)
            | _ -> Error "Architecture report has an invalid product edge.")

    let private ordinal values =
        values
        |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))

    let private ordinalEdges values =
        values
        |> List.sortWith (fun (leftSource, leftTarget) (rightSource, rightTarget) ->
            let source = StringComparer.Ordinal.Compare(leftSource, rightSource)

            if source = 0 then
                StringComparer.Ordinal.Compare(leftTarget, rightTarget)
            else
                source)

    let private validateEntries (element: JsonElement) =
        match array "assemblies" element, array "edges" element with
        | Ok assemblies, Ok edges ->
            match traverse assembly assemblies, traverse edge edges with
            | Ok names, Ok connections when
                names = ordinal names
                && Set.ofList names = requiredAssemblies
                && not connections.IsEmpty
                && connections = ordinalEdges connections
                && (connections |> Set.ofList |> Set.count) = connections.Length
                ->
                Ok()
            | _ ->
                Error "Architecture report assembly or edge inventory is incomplete or unordered."
        | _ -> Error "Architecture report has missing inventory arrays."

    let validateBytes (bytes: byte array) =
        if bytes.Length = 0 || bytes.Length > maximumBytes then
            Error "Architecture report exceeds its bounded size or is empty."
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                let root = document.RootElement

                exactProperties
                    (set [ "format"; "formatVersion"; "configuration"; "assemblies"; "edges" ])
                    root
                |> Result.bind (fun () ->
                    match
                        text "format" root, integer "formatVersion" root, text "configuration" root
                    with
                    | Ok "claimcore-architecture-inspection", Ok 1, Ok "Debug" ->
                        validateEntries root
                    | _ -> Error "Architecture report format or configuration is invalid.")
            with :? JsonException ->
                Error "Architecture report is not valid JSON."

    let validateDownloaded (root: RepositoryRoot) (stage: StageManifest) =
        if
            not (
                set [ "architecture-linux"; "architecture-macos"; "architecture-windows" ]
                |> Set.contains stage.StageId
            )
        then
            Error "Architecture report stage identity is invalid."
        else
            let matches = stage.Output.Files |> List.filter (fun file -> file.Path = fileName)

            match matches with
            | [ entry ] ->
                let relative = "artifacts/evidence-inputs/" + stage.StageId + "/" + fileName

                match
                    Repository.registeredPath root relative
                    |> Result.bind (Repository.ensureExistingSafe root)
                with
                | Error _ -> Error "Architecture report download is missing or unsafe."
                | Ok path ->
                    try
                        let length = FileInfo(path).Length

                        if
                            length <> entry.Length
                            || length > int64 maximumBytes
                            || Repository.sha256File path <> entry.Sha256
                        then
                            Error "Architecture report differs from its producer manifest."
                        else
                            File.ReadAllBytes(path) |> validateBytes
                    with _ ->
                        Error "Architecture report download could not be validated."
            | _ -> Error "Architecture stage lacks exactly one report."
