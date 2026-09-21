module ClaimCore.ArchitectureTests.ProjectReferences

open System
open System.IO
open System.Xml.Linq

let private ordinal (left: string) right =
    StringComparer.Ordinal.Compare(left, right)

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

/// Declared edges must match the reviewed permission set exactly. A missing declaration is a stale
/// permission and a surplus declaration is an unreviewed edge; both fail.
let violations
    (projectNames: Map<string, string>)
    (permissions: Map<string, string list>)
    (projectPath: string)
    (document: XDocument)
    =
    let source = nonNull (Path.GetFileNameWithoutExtension projectPath)
    let allowed = permissions |> Map.find source

    let unclassified, declared =
        includedPaths projectPath document
        |> List.map (fun targetPath -> Map.tryFind targetPath projectNames)
        |> List.partition Option.isNone

    let declaredSet = declared |> List.choose id |> Set.ofList
    let allowedSet = Set.ofList allowed

    [
        if not unclassified.IsEmpty then
            source + " declares an unclassified project reference"

        for surplus in Set.difference declaredSet allowedSet |> Set.toList |> List.sortWith ordinal do
            source + " declares a forbidden project reference: " + surplus

        for stale in Set.difference allowedSet declaredSet |> Set.toList |> List.sortWith ordinal do
            source + " is permitted an undeclared project reference: " + stale
    ]

let loadProjects root =
    Directory.GetFiles(root, "*.fsproj", SearchOption.AllDirectories)
    |> Array.map Path.GetFullPath
    |> Array.toList

/// Every project the repository actually builds, excluding generated output trees.
let loadRepositoryProjects repositoryRoot =
    [ "src"; "eng"; "tests" ]
    |> List.collect (fun area -> loadProjects (Path.Combine(repositoryRoot, area)))
    |> List.sortWith ordinal

let names (projects: string list) =
    projects
    |> List.map (fun path -> path, nonNull (Path.GetFileNameWithoutExtension path))
    |> Map.ofList
