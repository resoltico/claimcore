module ClaimCore.ContractGenerator.Program

open System
open System.IO
open ClaimCore.ContractGeneration

let private outputDirectory arguments =
    match arguments with
    | [| "--output"; directory |] when not (String.IsNullOrWhiteSpace directory) ->
        Path.GetFullPath directory
    | _ -> invalidArg (nameof arguments) "Use --output <directory>."

[<EntryPoint>]
let main arguments =
    let output = outputDirectory arguments
    Directory.CreateDirectory output |> ignore

    ContractArtifacts.all ()
    |> List.iter (fun artifact ->
        File.WriteAllBytes(Path.Combine(output, artifact.Name), artifact.Bytes))

    0
