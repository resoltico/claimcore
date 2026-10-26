namespace ClaimCore.ContractGeneration

open System
open System.Reflection
open System.IO
open System.Text.RegularExpressions
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

    let private root () =
        let rec find directory =
            if File.Exists(Path.Combine(directory, "ClaimCore.slnx")) then
                Some directory
            else
                match Directory.GetParent directory with
                | null -> None
                | parent -> find parent.FullName

        find (Directory.GetCurrentDirectory())
        |> Option.orElseWith (fun () -> find AppContext.BaseDirectory)
        |> Option.defaultWith (fun () ->
            invalidOp "Run the contract generator in a ClaimCore checkout.")

    let references renderer =
        let directory =
            Path.Combine(
                root (),
                if renderer = "browser" then
                    "web/src"
                else
                    "src/ClaimCore.Cli"
            )

        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        |> Seq.filter (fun path ->
            not (
                path.Split(Path.DirectorySeparatorChar)
                |> Array.exists (fun part -> List.contains part [ "generated"; "obj"; "bin" ])
            )
            && List.contains
                (Path.GetExtension path)
                (if renderer = "browser" then
                     [ ".ts"; ".tsx" ]
                 else
                     [ ".fs" ]))
        |> Seq.collect (fun path ->
            Regex.Matches(File.ReadAllText path, "[\"']((?:ui|cli)\\.[A-Za-z0-9_.-]+)[\"']")
            |> Seq.map (fun matched -> matched.Groups[1].Value))
        |> Set.ofSeq
