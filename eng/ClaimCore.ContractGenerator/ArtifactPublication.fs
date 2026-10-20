namespace ClaimCore.ContractGeneration

open System
open System.IO

[<RequireQualifiedAccess>]
type ArtifactRole =
    | RuntimePrecursor
    | QualificationOrTooling
    | IdentityDerived

type Artifact =
    {
        Name: string
        Bytes: byte array
        Role: ArtifactRole
    }

/// The produced set is the inventory. Unexpected content is never deleted.
module ArtifactPublication =
    let private admitExisting output (artifacts: Artifact list) =
        let owned = artifacts |> List.map _.Name |> Set.ofList

        if owned.Count <> artifacts.Length then
            invalidOp "Duplicate generated artifact name."

        for name in owned do
            if Path.GetFileName name <> name then
                invalidOp "Invalid generated artifact name."

        if Directory.Exists output then
            for entry in Directory.EnumerateFileSystemEntries output do
                if Directory.Exists entry || not (Set.contains (Path.GetFileName entry) owned) then
                    invalidOp "Output contains content outside the produced contract artifact set."

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
