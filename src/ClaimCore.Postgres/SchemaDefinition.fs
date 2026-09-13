namespace ClaimCore.Postgres

open System
open System.Globalization
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type internal MigrationDefinition =
    {
        Version: int
        Name: string
        Script: string
        Digest: string
    }

type private SchemaResourceAnchor = class end

/// Ordered, checksum-bound migrations embedded in the PostgreSQL adapter.
module internal SchemaDefinition =
    let private resourcePrefix = "ClaimCore.Migrations."
    let private manifestResource = resourcePrefix + "manifest.json"
    let private digestPattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private readResource (assembly: Assembly) resourceName =
        match assembly.GetManifestResourceStream(resourceName) |> Option.ofObj with
        | None ->
            raise (InvalidDataException("An embedded migration disappeared during discovery."))
        | Some stream ->
            use source = stream
            use reader = new StreamReader(source, UTF8Encoding(false, true), true)
            reader.ReadToEnd()

    let private parseResourceName (resourceName: string) =
        let fileName = resourceName.Substring(resourcePrefix.Length)
        let separator = fileName.IndexOf('_')

        if separator <> 3 || not (fileName.EndsWith(".sql", StringComparison.Ordinal)) then
            raise (InvalidDataException("Migration resources must use NNN_name.sql names."))

        let versionText = fileName.Substring(0, separator)
        let name = fileName.Substring(0, fileName.Length - 4)

        match Int32.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, version when version > 0 -> version, name
        | _ ->
            raise (
                InvalidDataException(
                    "Migration resources must start with a positive three-digit version."
                )
            )

    let private exactProperties expected (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            false
        else
            let actual = element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

            actual = Set.ofList expected
            && element.EnumerateObject() |> Seq.length = actual.Count

    let private manifestEntry (element: JsonElement) =
        let mutable version = 0
        let mutable name = JsonElement()
        let mutable sha256 = JsonElement()

        if
            not (exactProperties [ "version"; "name"; "sha256" ] element)
            || not (element.GetProperty("version").TryGetInt32(&version))
            || version <= 0
            || not (element.TryGetProperty("name", &name))
            || name.ValueKind <> JsonValueKind.String
            || not (element.TryGetProperty("sha256", &sha256))
            || sha256.ValueKind <> JsonValueKind.String
        then
            raise (InvalidDataException("The frozen migration manifest has an invalid entry."))

        match name.GetString() |> Option.ofObj, sha256.GetString() |> Option.ofObj with
        | Some entryName, Some digest when
            entryName.StartsWith($"{version:D3}_", StringComparison.Ordinal)
            && digestPattern.IsMatch(digest)
            ->
            version, entryName, digest
        | _ -> raise (InvalidDataException("The frozen migration manifest has an invalid entry."))

    let private readManifest (assembly: Assembly) =
        use document =
            JsonDocument.Parse(
                readResource assembly manifestResource,
                JsonDocumentOptions(
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                )
            )

        let root = document.RootElement
        let mutable schemaVersion = 0

        if
            not (exactProperties [ "schemaVersion"; "migrations" ] root)
            || not (root.GetProperty("schemaVersion").TryGetInt32(&schemaVersion))
            || schemaVersion <> 1
            || root.GetProperty("migrations").ValueKind <> JsonValueKind.Array
        then
            raise (InvalidDataException("The frozen migration manifest is invalid."))

        root.GetProperty("migrations").EnumerateArray()
        |> Seq.map manifestEntry
        |> Seq.toList

    let private migration assembly resourceName =
        let version, name = parseResourceName resourceName
        let script = readResource assembly resourceName

        {
            Version = version
            Name = name
            Script = script
            Digest = script |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexStringLower
        }

    let private validateSequence (migrations: MigrationDefinition list) =
        if List.isEmpty migrations then
            raise (InvalidDataException("At least one embedded database migration is required."))

        migrations
        |> List.iteri (fun index item ->
            let expected = index + 1

            if
                item.Version <> expected
                || not (item.Name.StartsWith($"{expected:D3}_", StringComparison.Ordinal))
            then
                raise (
                    InvalidDataException(
                        "Migration versions must be unique, contiguous, and canonically named from 001."
                    )
                ))

    let private discover (assembly: Assembly) =
        let migrations =
            assembly.GetManifestResourceNames()
            |> Array.filter (fun name ->
                name.StartsWith(resourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal))
            |> Array.map (migration assembly)
            |> Array.sortBy _.Version
            |> Array.toList

        validateSequence migrations
        let frozen = readManifest assembly

        let identity =
            migrations |> List.map (fun item -> item.Version, item.Name, item.Digest)

        if frozen <> identity then
            raise (
                InvalidDataException(
                    "Embedded migrations do not match the frozen append-only manifest."
                )
            )

        migrations

    let private discovered = lazy (discover typeof<SchemaResourceAnchor>.Assembly)

    let all () = discovered.Value
    let currentVersion () = all () |> List.last |> _.Version
