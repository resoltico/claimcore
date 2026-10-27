module internal ClaimCore.Tests.Architecture.ProductModel

open System.IO
open ClaimCore.Tests.Architecture.CompilerModel

let products =
    lazy
        (let known =
            List.zip
                [ "ClaimCore.Application"; "ClaimCore.RecordFormat"; "ClaimCore.Postgres" ]
                core.Value
            |> Map.ofList
            |> Map.add "ClaimCore.Domain" domain.Value

         Directory.EnumerateFiles(
             Path.Combine(ClaimCore.TestSupport.RepositoryRoot.find (), "src"),
             "*.fsproj",
             SearchOption.AllDirectories
         )
         |> Seq.sort
         |> Seq.map (fun path ->
             let name = Path.GetFileNameWithoutExtension path |> nonNull

             match Map.tryFind name known with
             | Some model -> model
             | None ->
                 load (Path.GetRelativePath(ClaimCore.TestSupport.RepositoryRoot.find (), path)))
         |> Seq.toList)

let owner (key: string) =
    match key.IndexOf('/') with
    | -1 -> key
    | position -> key.Substring(0, position)
