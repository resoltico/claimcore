namespace ClaimCore.Postgres

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type internal SchemaBaselineDefinition =
    {
        Id: string
        Script: string
        Digest: string
    }

type private SchemaResourceAnchor = class end

/// One reviewed final-state definition, never an ordered upgrade or a compatibility catalog.
module internal SchemaDefinition =
    let private readResource (assembly: Assembly) name =
        match assembly.GetManifestResourceStream(name) |> Option.ofObj with
        | None -> raise (InvalidDataException("An embedded schema baseline resource is missing."))
        | Some stream ->
            use source = stream
            use buffer = new MemoryStream()
            source.CopyTo(buffer)
            buffer.ToArray()

    let private exactProperties (expected: string list) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = expected.Length && Set.ofList names = Set.ofList expected

    let private identity (bytes: byte array) =
        use document = JsonDocument.Parse(bytes)
        let root = document.RootElement
        let mutable version = 0

        if
            not (exactProperties [ "schemaVersion"; "baselineId"; "sha256" ] root)
            || root.GetProperty("schemaVersion").ValueKind <> JsonValueKind.Number
            || not (root.GetProperty("schemaVersion").TryGetInt32(&version))
            || version <> 1
            || root.GetProperty("baselineId").ValueKind <> JsonValueKind.String
            || root.GetProperty("sha256").ValueKind <> JsonValueKind.String
        then
            raise (InvalidDataException("The frozen schema baseline identity is invalid."))

        let id =
            root.GetProperty("baselineId").GetString()
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                raise (InvalidDataException("Baseline text is missing.")))

        let digest =
            root.GetProperty("sha256").GetString()
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                raise (InvalidDataException("Baseline text is missing.")))

        if
            not (Regex.IsMatch(id, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant))
            || not (Regex.IsMatch(digest, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
        then
            raise (InvalidDataException("The frozen schema baseline identity is invalid."))

        id, digest

    let private discover () =
        let assembly = typeof<SchemaResourceAnchor>.Assembly
        let id, expected = readResource assembly "ClaimCore.SchemaBaseline.json" |> identity
        let bytes = readResource assembly "ClaimCore.SchemaBaseline.sql"
        let digest = bytes |> SHA256.HashData |> Convert.ToHexStringLower

        if digest <> expected then
            raise (
                InvalidDataException("The embedded SQL differs from its frozen baseline digest.")
            )

        {
            Id = id
            Script = UTF8Encoding(false, true).GetString(bytes)
            Digest = digest
        }

    let private definition = lazy (discover ())
    let current () = definition.Value
