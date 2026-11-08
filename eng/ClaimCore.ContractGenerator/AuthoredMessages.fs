namespace ClaimCore.ContractGeneration

open System
open System.Reflection
open System.IO
open System.Text.RegularExpressions
open System.Text.Json

/// Resource I/O is isolated from the generator's testable catalog admission.
module AuthoredMessages =
    type Translation = { Text: string; Source: string }

    type Issue =
        {
            Language: string
            Key: string
            Reason: string
        }

    let sourceHash (text: string) =
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes text)
        |> System.Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()[..11]

    /// Resolve at generation time: an old translation never silently accompanies new English.
    let resolve language (english: Map<string, string>) (translated: Map<string, Translation>) =
        for KeyValue(key, _) in translated do
            if not (Map.containsKey key english) then
                invalidOp ("Unknown translation key: " + language + " " + key)

        let resolved =
            english
            |> Map.map (fun key original ->
                match Map.tryFind key translated with
                | Some entry when entry.Source = sourceHash original -> entry.Text
                | _ -> original)

        let issues =
            english
            |> Map.toList
            |> List.choose (fun (key, original) ->
                let reason =
                    match Map.tryFind key translated with
                    | None -> Some "missing"
                    | Some entry when entry.Source <> sourceHash original -> Some "stale"
                    | _ -> None

                reason
                |> Option.map (fun reason ->
                    {
                        Language = language
                        Key = key
                        Reason = reason
                    }))

        resolved, issues

    let private unique owner pairs =
        let entries = Seq.toList pairs
        let result = Map.ofList entries

        if result.Count <> entries.Length then
            invalidOp ("Duplicate localization key: " + owner)

        result

    let private resource (assembly: Assembly) name =
        use stream = assembly.GetManifestResourceStream(name) |> nonNull
        use document = JsonDocument.Parse stream

        document.RootElement.EnumerateObject()
        |> Seq.map (fun item -> item.Name, item.Value.Clone())
        |> unique name

    let private admitted =
        lazy
            (let assembly = Assembly.GetExecutingAssembly()

             let raw =
                 assembly.GetManifestResourceNames()
                 |> Array.filter (fun name ->
                     name.StartsWith("ClaimCore.Localization.", StringComparison.Ordinal))
                 |> Array.groupBy (fun name -> name.Split('.')[2])
                 |> Array.map (fun (language, names) ->
                     language,
                     names |> Seq.collect (resource assembly >> Map.toSeq) |> unique language)
                 |> Map.ofArray

             let english = raw["en"] |> Map.map (fun _ value -> value.GetString() |> nonNull)

             for language in Map.keys raw do
                 if
                     not (
                         List.contains language ClaimCore.Presentation.LocalizationPolicy.languages
                     )
                 then
                     invalidOp ("Unknown localization language: " + language)

             let catalogs =
                 ClaimCore.Presentation.LocalizationPolicy.languages
                 |> List.map (fun language ->
                     let entries = Map.tryFind language raw |> Option.defaultValue Map.empty

                     language,
                     (if language = "en" then
                          english, []
                      else
                          let translated =
                              entries
                              |> Map.map (fun key value ->
                                  if
                                      value.ValueKind <> JsonValueKind.Object
                                      || (value.EnumerateObject() |> Seq.length) <> 2
                                      || (value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
                                         <> set [ "text"; "source" ]
                                  then
                                      invalidOp (
                                          "Translation requires text and source: "
                                          + language
                                          + " "
                                          + key
                                      )

                                  let text = value.GetProperty("text").GetString() |> nonNull
                                  let source = value.GetProperty("source").GetString() |> nonNull

                                  if
                                      not (
                                          Regex.IsMatch(
                                              source,
                                              @"^[0-9a-f]{12}\z",
                                              RegexOptions.CultureInvariant
                                          )
                                      )
                                  then
                                      invalidOp (
                                          "Invalid English source hash: " + language + " " + key
                                      )

                                  { Text = text; Source = source })

                          resolve language english translated))
                 |> Map.ofList

             catalogs)

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
