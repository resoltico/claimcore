module PublicationRestoreGraph

open System
open System.Diagnostics
open System.IO
open System.Threading
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

let private generate root project configuration artifacts runtime framework sdkHost output =
    let start = ProcessStartInfo(sdkHost)
    start.WorkingDirectory <- root
    start.Environment["DOTNET_ROOT"] <- Path.GetDirectoryName sdkHost
    start.Environment["DOTNET_HOST_PATH"] <- sdkHost
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
            "-property:ArtifactsPath=" + artifacts
            "-property:RuntimeIdentifier=" + runtime
            "-property:TargetFramework=" + framework
            "-property:RestoreGraphOutputPath=" + output
        ] do
        start.ArgumentList.Add argument

    use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 60.)
    use child = Process.Start start

    let stdout =
        child.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token)

    let stderr = child.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token)

    if not (child.WaitForExit 60_000) then
        child.Kill true
        child.WaitForExit(2000) |> ignore
        deadline.Cancel()
        failwith "Producing dependency graph timed out."

    let drained = System.Threading.Tasks.Task.WhenAll(stdout, stderr)

    try
        drained.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
    with _ ->
        deadline.Cancel()

        failwith (
            "Producing dependency graph delivery refused; child exit "
            + string child.ExitCode
            + "."
        )

    if child.ExitCode <> 0 then
        failwith "Producing dependency graph unavailable."

let private normalize (project: JsonNode) =
    let result = project.DeepClone()
    let restore = result["restore"]
    let mutable properties = Unchecked.defaultof<JsonNode>

    if restore.AsObject().TryGetPropertyValue("restoreLockProperties", &properties) then
        properties.AsObject().Remove("restoreLockedMode") |> ignore

    result

let verify
    root
    (project: string)
    (assetsPath: string)
    configuration
    artifacts
    runtime
    framework
    sdkHost
    =
    let lock = Path.Combine(Path.GetDirectoryName project, "packages.lock.json")

    let graphPath =
        Path.Combine(Path.GetDirectoryName assetsPath, "claimcore-current-restore-graph.json")

    generate root project configuration artifacts runtime framework sdkHost graphPath
    use assets = JsonDocument.Parse(File.ReadAllText assetsPath)

    if packageLock lock <> actualPackages assets.RootElement then
        failwith "Restored packages differ from the current lock."

    let projects = JsonNode.Parse(File.ReadAllText graphPath)["projects"]
    let current = projects[project]
    let restored = JsonNode.Parse(File.ReadAllText assetsPath)["project"]

    if not (JsonNode.DeepEquals(normalize current, normalize restored)) then
        failwith "Restored dependency declarations differ from current producing inputs."
