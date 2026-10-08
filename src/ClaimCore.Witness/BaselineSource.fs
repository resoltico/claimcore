namespace ClaimCore.Witness

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions

[<NoEquality; NoComparison>]
type internal BaselineSourceDefinition =
    {
        Id: string
        Digest: string
        Bytes: byte array
    }

/// Ordered fresh-schema source bytes, never an upgrade sequence.
module internal BaselineSource =
    let private text (value: JsonElement) =
        value.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () ->
            raise (InvalidDataException("Baseline source text is missing.")))

    let private requireProperties (root: JsonElement) =
        let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        let expected = [ "schemaVersion"; "baselineId"; "sha256"; "fragments" ]

        if names.Length <> expected.Length || Set.ofList names <> Set.ofList expected then
            raise (InvalidDataException("Baseline source manifest shape is invalid."))

    let private fragments (root: JsonElement) =
        let paths =
            root.GetProperty("fragments").EnumerateArray() |> Seq.map text |> Seq.toList

        if
            paths.IsEmpty
            || paths.Length <> (Set.ofList paths).Count
            || paths
               |> List.exists (fun path ->
                   not (
                       Regex.IsMatch(
                           path,
                           "^[a-z][a-z0-9-]*[.]sql$",
                           RegexOptions.CultureInvariant
                       )
                   ))
        then
            raise (InvalidDataException("Baseline source fragment names are invalid."))

        paths

    let private resource (assembly: Assembly) name =
        use stream =
            assembly.GetManifestResourceStream(name)
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                raise (InvalidDataException("Baseline source fragment is missing.")))

        use buffer = new MemoryStream()
        stream.CopyTo(buffer)
        buffer.ToArray()

    let assemble (assembly: Assembly) (markerBytes: byte array) (prefix: string) =
        use document = JsonDocument.Parse(markerBytes)
        let root = document.RootElement
        requireProperties root

        if root.GetProperty("schemaVersion").GetInt32() <> 2 then
            raise (InvalidDataException("Baseline source manifest version is unsupported."))

        let paths = fragments root
        let expected = paths |> List.map (fun path -> prefix + path) |> Set.ofList

        let actual =
            assembly.GetManifestResourceNames()
            |> Array.filter (fun name -> name.StartsWith(prefix, StringComparison.Ordinal))
            |> Set.ofArray

        if actual <> expected then
            raise (InvalidDataException("Baseline source resources differ from the manifest."))

        let bytes =
            paths
            |> List.collect (fun path -> resource assembly (prefix + path) |> Array.toList)
            |> List.toArray

        let digest = SHA256.HashData(bytes) |> Convert.ToHexStringLower

        if root.GetProperty("sha256").GetString() <> digest then
            raise (InvalidDataException("Baseline source digest mismatch."))

        let id = root.GetProperty("baselineId") |> text

        if not (Regex.IsMatch(id, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)) then
            raise (InvalidDataException("Baseline source identity is invalid."))

        {
            Id = id
            Digest = digest
            Bytes = bytes
        }
