namespace ClaimCore.ContractGeneration

open System
open System.Reflection
open System.Text.Json

/// Resource I/O is isolated from the generator's testable catalog admission.
module AuthoredMessages =
    let private entries (assembly: Assembly) name =
        use stream = assembly.GetManifestResourceStream(name) |> nonNull
        use document = JsonDocument.Parse stream

        document.RootElement.EnumerateObject()
        |> Seq.map (fun item -> item.Name, item.Value.GetString() |> nonNull)
        |> Seq.toList

    let all () =
        let assembly = Assembly.GetExecutingAssembly()

        assembly.GetManifestResourceNames()
        |> Array.filter (fun name ->
            name.StartsWith("ClaimCore.Localization.", StringComparison.Ordinal))
        |> Array.groupBy (fun name -> name.Split('.')[2])
        |> Array.map (fun (language, names) ->
            language, names |> Array.toList |> List.collect (entries assembly))
        |> Map.ofArray
