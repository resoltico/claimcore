module ClaimCore.ArchitectureTests.ProjectReferences

open System
open System.IO
open System.Xml.Linq

let private includedPaths (projectPath: string) (document: XDocument) =
    let directory =
        Path.GetDirectoryName projectPath
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Project path has no directory.")

    document.Descendants()
    |> Seq.filter (fun element -> element.Name.LocalName = "ProjectReference")
    |> Seq.map (fun element ->
        let includeValue =
            element.Attribute(XName.Get "Include")
            |> Option.ofObj
            |> Option.map (fun attribute -> attribute.Value)
            |> Option.defaultWith (fun () -> invalidOp "ProjectReference has no Include.")

        if String.IsNullOrWhiteSpace includeValue then
            invalidOp "ProjectReference has an empty Include."

        Path.GetFullPath(Path.Combine(directory, includeValue)))
    |> Seq.toList

let violations
    (projectNames: Map<string, string>)
    (permissions: Map<string, string list>)
    (projectPath: string)
    (document: XDocument)
    =
    let source = nonNull (Path.GetFileNameWithoutExtension projectPath)
    let allowed = permissions |> Map.find source

    includedPaths projectPath document
    |> List.choose (fun targetPath ->
        match Map.tryFind targetPath projectNames with
        | None -> Some(source + " declares an unclassified project reference")
        | Some target when not (List.contains target allowed) ->
            Some(source + " declares a forbidden project reference: " + target)
        | Some _ -> None)

let loadProjects root =
    Directory.GetFiles(root, "*.fsproj", SearchOption.AllDirectories)
    |> Array.map Path.GetFullPath
    |> Array.toList

let names (projects: string list) =
    projects
    |> List.map (fun path -> path, nonNull (Path.GetFileNameWithoutExtension path))
    |> Map.ofList
