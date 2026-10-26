module ClaimCore.TestSupport.RepositoryRoot

open System
open System.IO

let private isRepositoryRoot (directory: string) =
    File.Exists(Path.Combine(directory, "ClaimCore.slnx"))
    && File.Exists(Path.Combine(directory, "Directory.Build.props"))
    && File.Exists(Path.Combine(directory, "tests/ClaimCore.Tests/ClaimCore.Tests.fsproj"))

let tryFindFrom (startDirectory: string) =
    if
        String.IsNullOrWhiteSpace(startDirectory)
        || not (Path.IsPathFullyQualified(startDirectory))
    then
        None
    else
        let rec search (directory: DirectoryInfo) =
            if isRepositoryRoot directory.FullName then
                Some directory.FullName
            else
                match directory.Parent with
                | null -> None
                | parent -> search parent

        search (DirectoryInfo(Path.GetFullPath(startDirectory)))

let find () =
    [ AppContext.BaseDirectory; Environment.CurrentDirectory ]
    |> List.tryPick tryFindFrom
    |> Option.defaultWith (fun () ->
        invalidOp "Could not locate the ClaimCore repository root from test runtime directories.")
