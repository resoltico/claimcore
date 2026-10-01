namespace ClaimCore.Docs

open System
open System.IO
open System.Text.RegularExpressions

/// A contract named by a heading is exercised by tests whose names carry its `[CC-...]` token, and
/// a token in a test name must name a declared contract. The generated inventories under
/// `tests/inventory` are the only place test names are read from.
[<RequireQualifiedAccess>]
module ContractTokens =
    let private token =
        Regex(@"\[(CC-[A-Z]{2,12}-[0-9]{3})\]", RegexOptions.CultureInvariant)

    let private tokensIn (path: string) =
        File.ReadAllLines(path)
        |> Seq.collect (fun line -> token.Matches(line) |> Seq.map _.Groups[1].Value)

    let verify (root: RepositoryRoot) (contracts: ContractDeclaration list) =
        let directory = Path.Combine(root.Path, "tests", "inventory")

        if not (Directory.Exists(directory)) then
            Error
                [
                    Diagnostic.create
                        DiagnosticCode.InvalidContract
                        "tests/inventory does not exist."
                ]
        else
            let named =
                Directory.EnumerateFiles(directory, "*.txt")
                |> Seq.collect tokensIn
                |> Set.ofSeq

            let declared = contracts |> List.map _.Id |> Set.ofList

            let untested =
                declared - named
                |> Set.toList
                |> List.map (fun id ->
                    Diagnostic.create
                        DiagnosticCode.InvalidContract
                        $"Contract '{id}' is not named by any test; name at least one test '[{id}] ...'.")

            let unknown =
                named - declared
                |> Set.toList
                |> List.map (fun id ->
                    Diagnostic.create
                        DiagnosticCode.InvalidContract
                        $"A test names '[{id}]', which no documentation heading declares.")

            match untested @ unknown with
            | [] -> Ok()
            | errors -> Error errors
