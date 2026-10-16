namespace ClaimCore.ContractGeneration

open System
open System.IO
open System.Text.Json

type Artifact = { Name: string; Bytes: byte array }

/// Only a previously generated inventory authorizes removing an obsolete output.
module ArtifactPublication =
    let inventoryName = "artifact-inventory.json"

    let withInventory (artifacts: Artifact list) =
        let names = artifacts |> List.map _.Name |> List.sort

        if names.Length <> (names |> Set.ofList |> Set.count) then
            invalidOp "Duplicate generated artifact name."

        for name in names do
            if Path.GetFileName name <> name || name = inventoryName then
                invalidOp "Invalid generated artifact name."

        artifacts
        @ [
            {
                Name = inventoryName
                Bytes = JsonSerializer.SerializeToUtf8Bytes names
            }
        ]

    let private admitExisting output (artifacts: Artifact list) =
        if Directory.Exists output then
            let inventory = Path.Combine(output, inventoryName)

            let owned =
                if File.Exists inventory then
                    JsonSerializer.Deserialize<string list>(File.ReadAllBytes inventory)
                    |> nonNull
                    |> Set.ofList
                    |> Set.add inventoryName
                else
                    artifacts |> List.map _.Name |> Set.ofList

            for entry in Directory.EnumerateFileSystemEntries output do
                if Directory.Exists entry || not (Set.contains (Path.GetFileName entry) owned) then
                    invalidOp "Output contains content outside its generated artifact inventory."

    let private stage directory (artifacts: Artifact list) =
        Directory.CreateDirectory directory |> ignore

        for artifact in artifacts do
            File.WriteAllBytes(Path.Combine(directory, artifact.Name), artifact.Bytes)

        let actual =
            Directory.GetFiles directory |> Array.map Path.GetFileName |> Set.ofArray

        if actual <> (artifacts |> List.map _.Name |> Set.ofList) then
            invalidOp "The staged generated artifact inventory differs."

        for artifact in artifacts do
            if File.ReadAllBytes(Path.Combine(directory, artifact.Name)) <> artifact.Bytes then
                invalidOp "The staged generated artifact bytes differ."

    let publish output artifacts =
        let output = Path.GetFullPath output
        admitExisting output artifacts
        let parent = Path.GetDirectoryName output |> nonNull
        Directory.CreateDirectory parent |> ignore
        let staging = Path.Combine(parent, ".contract-stage-" + Guid.NewGuid().ToString "N")
        let backup = Path.Combine(parent, ".contract-backup-" + Guid.NewGuid().ToString "N")

        try
            stage staging artifacts

            if Directory.Exists output then
                Directory.Move(output, backup)

            try
                Directory.Move(staging, output)
            with _ ->
                if Directory.Exists backup then
                    Directory.Move(backup, output)

                reraise ()

            if Directory.Exists backup then
                Directory.Delete(backup, true)
        finally
            if Directory.Exists staging then
                Directory.Delete(staging, true)
