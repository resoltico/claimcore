module ClaimCore.ContractGenerator.Program

open System
open System.IO
open ClaimCore.ContractGeneration
open ClaimCore.Contracts

let private outputDirectory arguments =
    match arguments with
    | [| "--output"; directory |] when not (String.IsNullOrWhiteSpace(directory)) ->
        Path.GetFullPath(directory)
    | _ -> invalidArg (nameof arguments) "Use --output <directory>."

let private write outputDirectory (artifact: Artifact) =
    File.WriteAllBytes(Path.Combine(outputDirectory, artifact.Name), artifact.Bytes)

[<EntryPoint>]
let main arguments =
    let output = outputDirectory arguments
    Directory.CreateDirectory(output) |> ignore
    let projection = ContractProjection.current ()
    let artifacts = ContractArtifacts.all projection
    artifacts |> List.iter (write output)

    File.WriteAllBytes(
        Path.Combine(output, "convergence-manifest.json"),
        ContractArtifacts.manifest artifacts projection
    )

    0
