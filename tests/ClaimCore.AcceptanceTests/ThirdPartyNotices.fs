module internal ClaimCore.AcceptanceTests.ThirdPartyNotices

open System
open System.IO
open ThirdPartyNoticeManifest

let private runtimeFile (path: string) =
    let extension = Path.GetExtension path
    List.contains extension [ ".dll"; ".so"; ".dylib"; ".a"; ".o" ]

let private explained name known (relative: string) =
    Set.contains relative known
    || relative = name + ".deps.json"
    || relative = name + ".runtimeconfig.json"
    || relative = name + ".staticwebassets.endpoints.json"
    || relative = "web.config"
    || relative = "LICENSE"
    || (relative.StartsWith("wwwroot/", StringComparison.Ordinal)
        && not (runtimeFile relative))
    || (Path.GetExtension relative = ".pdb"
        && Set.contains (Path.ChangeExtension(relative, ".dll") |> nonNull) known)

let qualify root directory project =
    let name = ThirdPartyNoticeAssets.assemblyName root project
    let manifest = delivered root directory
    let assets = ThirdPartyNoticeAssets.graph root directory name manifest
    let native = ThirdPartyNoticeAssets.host directory name manifest

    let notices =
        "third-party-notices.json" :: (manifest.Texts |> List.map _.File)
        |> List.map (fun file -> "licenses/" + file)

    let known =
        assets
        |> List.map _.Path
        |> Set.ofList
        |> Set.add name
        |> Set.add native
        |> Set.union (Set.ofList notices)

    for file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories) do
        let relative =
            Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/')

        require (explained name known relative)

    assets
    |> List.filter (fun asset ->
        manifest.Packages |> List.exists (fun item -> item.Identity = asset.Library))
    |> List.map _.Library
    |> Set.ofList
