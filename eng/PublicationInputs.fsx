#load "PublicationRestoreGraph.fsx"

open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Text
open System.Text.Json

let read path =
    use stream = File.OpenRead path
    use pe = new PEReader(stream)
    let metadata = pe.GetMetadataReader()

    let resource =
        metadata.ManifestResources
        |> Seq.map metadata.GetManifestResource
        |> Seq.filter (fun value -> metadata.GetString(value.Name) = "ClaimCore.ProducingInputs")
        |> Seq.exactlyOne

    if not resource.Implementation.IsNil then
        failwith "External producing-input resource refused."

    let block =
        pe.GetSectionData(pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress)

    let mutable reader =
        block.GetReader(int resource.Offset, block.Length - int resource.Offset)

    let length = reader.ReadInt32()

    if length <> 64 then
        failwith "Invalid producing-input resource."

    let digest = Encoding.UTF8.GetString(reader.ReadBytes length)

    if not (System.Text.RegularExpressions.Regex.IsMatch(digest, "^[0-9a-f]{64}$")) then
        failwith "Invalid producing-input digest."

    digest

let physicalPath root (relative: string) =
    if Path.IsPathRooted relative then
        failwith "Absolute producing input refused."

    let segments = relative.Split '/'

    if segments |> Array.exists (fun part -> part = "" || part = "." || part = "..") then
        failwith "Unsafe producing input refused."

    let mutable current = root

    for segment in segments do
        current <- Path.Combine(current, segment)

        if (File.GetAttributes current).HasFlag FileAttributes.ReparsePoint then
            failwith "Linked producing input refused."

    current

let inputDigest root =
    use document =
        JsonDocument.Parse(File.ReadAllText(physicalPath root "config/publication-inputs.json"))

    let entries (name: string) =
        document.RootElement.GetProperty(name).EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toArray

    let exclusions = entries "excludedDirectories"
    let extensions = entries "extensions" |> Set.ofArray

    let files =
        System.Collections.Generic.SortedSet<string>(entries "files", StringComparer.Ordinal)

    let excluded (path: string) =
        exclusions
        |> Array.exists (fun item ->
            path = item
            || path.StartsWith(item + "/", StringComparison.Ordinal)
            || Array.contains item (path.Split '/'))

    let rec walk directory =
        if
            (File.GetAttributes(Path.Combine(root, directory))).HasFlag FileAttributes.ReparsePoint
        then
            failwith "Linked publication inputs refused."

        for physical in Directory.EnumerateFileSystemEntries(physicalPath root directory) do
            let path = Path.GetRelativePath(root, physical).Replace('\\', '/')

            if not (excluded path) then
                let attributes = File.GetAttributes physical

                if attributes.HasFlag FileAttributes.ReparsePoint then
                    failwith "Linked publication inputs refused."

                if attributes.HasFlag FileAttributes.Directory then
                    walk path
                elif Set.contains (Path.GetExtension path) extensions then
                    files.Add path |> ignore

    entries "directories" |> Array.iter walk
    let records = StringBuilder()

    let hash bytes =
        Convert
            .ToHexString(System.Security.Cryptography.SHA256.HashData(bytes: byte array))
            .ToLowerInvariant()

    for path in files do
        let physical = physicalPath root path

        if (File.GetAttributes physical).HasFlag FileAttributes.ReparsePoint then
            failwith "Linked publication inputs refused."

        records.Append(path).Append('\000').Append(hash (File.ReadAllBytes physical)).Append('\n')
        |> ignore

    hash (Encoding.UTF8.GetBytes(records.ToString()))

try
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| "record"
         root
         destination
         sdk
         project
         assets
         configuration
         artifacts
         runtime
         framework
         sdkHost |] ->
        use specification =
            JsonDocument.Parse(File.ReadAllText(physicalPath (Path.GetFullPath root) "global.json"))

        if
            specification.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            <> sdk
        then
            failwith "Unsupported producing SDK."

        PublicationRestoreGraph.verify
            (Path.GetFullPath root)
            project
            assets
            configuration
            artifacts
            runtime
            framework
            sdkHost

        let digest = inputDigest (Path.GetFullPath root)

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath destination))
        |> ignore

        if not (File.Exists destination) || File.ReadAllText destination <> digest then
            File.WriteAllText(destination, digest, UTF8Encoding false)
    | [| "verify"; root; source |] ->
        if inputDigest (Path.GetFullPath root) <> File.ReadAllText source then
            failwith "Producing inputs changed during compilation."
    | [| "read"; directory |] ->
        let values =
            Directory.GetFiles(directory, "ClaimCore.*.dll", SearchOption.TopDirectoryOnly)
            |> Array.map (fun path -> Path.GetFileName path, read path)
            |> dict

        if values.Count = 0 then
            failwith "Missing product assemblies."

        Console.WriteLine(JsonSerializer.Serialize values)
    | _ -> failwith "Invalid producing-input command."
with _ ->
    Console.Error.WriteLine
        "Producing inputs refused. Run locked restore and rebuild with the pinned SDK; check physical source paths and publication format."

    Environment.Exit 1
