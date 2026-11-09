namespace ClaimCore.ContractGeneration

open System
open System.Reflection
open System.IO
open System.Text.RegularExpressions
open System.Text.Json

/// Resource I/O is isolated from the generator's testable catalog admission.
module AuthoredMessages =
    type Issue = ClaimCore.Presentation.LocalizationPolicy.Issue

    let private admitted =
        lazy
            (let assembly = Assembly.GetExecutingAssembly()

             let resources =
                 assembly.GetManifestResourceNames()
                 |> Array.filter (fun name ->
                     name.StartsWith("ClaimCore.Localization.", StringComparison.Ordinal))
                 |> Array.groupBy (fun name -> name.Split('.')[2])
                 |> Array.map (fun (language, names) ->
                     language,
                     names
                     |> Array.map (fun name ->
                         use stream = assembly.GetManifestResourceStream(name) |> nonNull
                         use bytes = new MemoryStream()
                         stream.CopyTo bytes
                         bytes.ToArray())
                     |> Array.toList)
                 |> Map.ofArray

             for language in Map.keys resources do
                 if
                     not (
                         List.contains language ClaimCore.Presentation.LocalizationPolicy.languages
                     )
                 then
                     invalidOp ("Unknown localization language: " + language)

             ClaimCore.Presentation.LocalizationPolicy.languages
             |> List.map (fun language ->
                 language,
                 ClaimCore.Presentation.LocalizationPolicy.load
                     language
                     resources["en"]
                     (Map.tryFind language resources |> Option.defaultValue []))
             |> Map.ofList)

    let all () =
        admitted.Value |> Map.map (fun _ (entries, _) -> Map.toList entries)

    let issues () =
        admitted.Value |> Map.toList |> List.collect (snd >> snd)

    let required (issues: Issue list) =
        issues
        |> List.filter (fun issue ->
            (ClaimCore.Presentation.LocalizationPolicy.languageOption issue.Language).required)

    let requiredIssues () = required (issues ())

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
