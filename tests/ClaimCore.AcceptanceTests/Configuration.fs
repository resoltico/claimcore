module ClaimCore.AcceptanceTests.Configuration

open System
open System.IO
open ClaimCore.TestSupport

[<NoEquality; NoComparison>]
type Inputs =
    {
        RepositoryRoot: string
        CliDirectory: string
        DatabaseDirectory: string
        CliManifest: string
        DatabaseManifest: string
    }

let private required name =
    match Environment.GetEnvironmentVariable(name) |> Option.ofObj with
    | Some value when not (String.IsNullOrWhiteSpace(value)) -> value
    | _ -> invalidOp ($"Acceptance prerequisite {name} is missing.")

let private regularDirectory (name: string) (raw: string) =
    if not (Path.IsPathFullyQualified(raw)) then
        invalidOp ($"Acceptance prerequisite {name} must be an absolute directory.")

    let path = Path.GetFullPath(raw)
    let info = DirectoryInfo(path)
    let attributes = info.Attributes

    if not info.Exists || attributes.HasFlag(FileAttributes.ReparsePoint) then
        invalidOp ($"Acceptance prerequisite {name} is not a regular directory.")

    path

let private regularFile (name: string) (raw: string) =
    if not (Path.IsPathFullyQualified(raw)) then
        invalidOp ($"Acceptance prerequisite {name} must be an absolute file.")

    let path = Path.GetFullPath(raw)
    let info = FileInfo(path)
    let attributes = info.Attributes

    if
        not info.Exists
        || attributes.HasFlag(FileAttributes.Directory)
        || attributes.HasFlag(FileAttributes.ReparsePoint)
    then
        invalidOp ($"Acceptance prerequisite {name} is not a regular file.")

    path

let private repositoryRoot = RepositoryRoot.find ()

let load () =
    let cliDirectory =
        required "CLAIMCORE_ACCEPTANCE_CLI_DIR"
        |> regularDirectory "CLAIMCORE_ACCEPTANCE_CLI_DIR"

    let databaseDirectory =
        required "CLAIMCORE_ACCEPTANCE_DATABASE_DIR"
        |> regularDirectory "CLAIMCORE_ACCEPTANCE_DATABASE_DIR"

    let cliManifest =
        required "CLAIMCORE_ACCEPTANCE_CLI_MANIFEST"
        |> regularFile "CLAIMCORE_ACCEPTANCE_CLI_MANIFEST"

    let databaseManifest =
        required "CLAIMCORE_ACCEPTANCE_DATABASE_MANIFEST"
        |> regularFile "CLAIMCORE_ACCEPTANCE_DATABASE_MANIFEST"

    {
        RepositoryRoot = repositoryRoot
        CliDirectory = cliDirectory
        DatabaseDirectory = databaseDirectory
        CliManifest = cliManifest
        DatabaseManifest = databaseManifest
    }
