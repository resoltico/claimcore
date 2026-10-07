module PublicationRestoreGraph

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

let private packageLock path =
    use document = JsonDocument.Parse(File.ReadAllText path)

    document.RootElement.GetProperty("dependencies").EnumerateObject()
    |> Seq.collect (fun framework -> framework.Value.EnumerateObject())
    |> Seq.filter (fun item -> item.Value.GetProperty("type").GetString() <> "Project")
    |> Seq.map (fun item ->
        let version = item.Value.GetProperty("resolved").GetString()

        (item.Name + "/" + version).ToLowerInvariant(),
        item.Value.GetProperty("contentHash").GetString())
    |> Map.ofSeq

let private actualPackages (assets: JsonElement) =
    assets.GetProperty("libraries").EnumerateObject()
    |> Seq.filter (fun item -> item.Value.GetProperty("type").GetString() = "package")
    |> Seq.map (fun item ->
        item.Name.ToLowerInvariant(), item.Value.GetProperty("sha512").GetString())
    |> Map.ofSeq

let private generate root project configuration output =
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true

    for argument in
        [
            "msbuild"
            project
            "-nologo"
            "-verbosity:quiet"
            "-maxcpucount:1"
            "-nodeReuse:false"
            "-target:GenerateRestoreGraphFile"
            "-property:RestoreLockedMode=true"
            "-property:Configuration=" + configuration
            "-property:RestoreGraphOutputPath=" + output
        ] do
        start.ArgumentList.Add argument

    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit 60_000) then
        child.Kill true
        failwith "Producing dependency graph timed out."

    stdout.GetAwaiter().GetResult() |> ignore
    stderr.GetAwaiter().GetResult() |> ignore

    if child.ExitCode <> 0 then
        failwith "Producing dependency graph unavailable."

let private normalize (project: JsonNode) =
    let result = project.DeepClone()
    let restore = result["restore"]
    let mutable properties = Unchecked.defaultof<JsonNode>

    if restore.AsObject().TryGetPropertyValue("restoreLockProperties", &properties) then
        properties.AsObject().Remove("restoreLockedMode") |> ignore

    result

let verify root (project: string) (assetsPath: string) configuration =
    let lock = Path.Combine(Path.GetDirectoryName project, "packages.lock.json")

    let graphPath =
        Path.Combine(Path.GetDirectoryName assetsPath, "claimcore-current-restore-graph.json")

    generate root project configuration graphPath
    use assets = JsonDocument.Parse(File.ReadAllText assetsPath)

    if packageLock lock <> actualPackages assets.RootElement then
        failwith "Restored packages differ from the current lock."

    let projects = JsonNode.Parse(File.ReadAllText graphPath)["projects"]
    let current = projects[project]
    let restored = JsonNode.Parse(File.ReadAllText assetsPath)["project"]

    if not (JsonNode.DeepEquals(normalize current, normalize restored)) then
        failwith "Restored dependency declarations differ from current producing inputs."
