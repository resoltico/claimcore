module ClaimCore.ContractGenerator.Program

open System
open System.IO
open ClaimCore.ContractGeneration
open ClaimCore.Contracts

let private outputDirectories arguments =
    match arguments with
    | [| "--output"; directory |] when not (String.IsNullOrWhiteSpace(directory)) ->
        Path.GetFullPath(directory), None
    | [| "--output"; directory; "--protocol-output"; protocol |] when
        not (String.IsNullOrWhiteSpace(directory))
        && not (String.IsNullOrWhiteSpace(protocol))
        ->
        let output = Path.GetFullPath(directory)
        let protocolOutput = Path.GetFullPath(protocol)

        if output = protocolOutput then
            invalidArg (nameof arguments) "Protocol and browser output must be separate."

        output, Some protocolOutput
    | _ -> invalidArg (nameof arguments) "Use --output <directory> [--protocol-output <directory>]."

let private write outputDirectory (artifact: Artifact) =
    File.WriteAllBytes(Path.Combine(outputDirectory, artifact.Name), artifact.Bytes)

[<EntryPoint>]
let main arguments =
    let output, protocol = outputDirectories arguments
    Directory.CreateDirectory(output) |> ignore
    let projection = ContractProjection.current ()
    let artifacts = ContractArtifacts.all projection
    artifacts |> List.iter (write output)

    File.WriteAllBytes(
        Path.Combine(output, "convergence-manifest.json"),
        ContractArtifacts.manifest artifacts projection
    )

    protocol
    |> Option.iter (fun directory ->
        let sources = DotNetProtocol.artifacts projection
        Directory.CreateDirectory(directory) |> ignore

        for name, bytes in sources do
            File.WriteAllBytes(Path.Combine(directory, name), bytes))

    0
