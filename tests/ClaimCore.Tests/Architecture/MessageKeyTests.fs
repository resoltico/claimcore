module ClaimCore.Tests.Architecture.MessageKeyTests

open System
open System.IO
open System.Collections
open Microsoft.FSharp.Reflection
open Expecto
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open ClaimCore.Tests.Architecture.NegativeFixtures

let private checker = lazy (FSharpChecker.Create())

let rec private literals (value: obj | null) =
    seq {
        match value with
        | null -> ()
        | :? SynConst as constant ->
            match constant with
            | SynConst.String(text, _, _) -> yield text
            | _ -> ()
        | :? SynInterpolatedStringPart as part ->
            match part with
            | SynInterpolatedStringPart.String(text, _) -> yield text
            | SynInterpolatedStringPart.FillExpr(expression, _) -> yield! literals expression
        | :? string -> ()
        | _ ->
            let value = nonNull value
            let kind = value.GetType()

            if FSharpType.IsTuple kind then
                for field in FSharpValue.GetTupleFields value do
                    yield! literals field
            elif FSharpType.IsUnion kind then
                let _, fields = FSharpValue.GetUnionFields(value, kind)

                for field in fields do
                    yield! literals field
            elif FSharpType.IsRecord kind then
                for field in FSharpValue.GetRecordFields value do
                    yield! literals field
            else
                match value with
                | :? IEnumerable as items ->
                    for item in items do
                        yield! literals item
                | _ -> ()
    }

let private violations paths =
    [
        for path in paths do
            let options =
                { FSharpParsingOptions.Default with
                    SourceFiles = [| path |]
                }

            let parsed =
                checker.Value.ParseFile(path, SourceText.ofString (File.ReadAllText path), options)
                |> Async.RunSynchronously

            let errors =
                parsed.Diagnostics
                |> Array.filter (fun error ->
                    error.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)

            Expect.isEmpty errors ("Source must parse completely: " + path)

            for text in literals parsed.ParseTree do
                if text.StartsWith("cli.", StringComparison.Ordinal) then
                    yield path, text
    ]

let private requireTyped paths =
    let found = violations paths
    Expect.isEmpty found ("CLI keys require typed producers: " + sprintf "%A" found)

[<Tests>]
let tests =
    testCase "CLI message keys have typed producers with a compiled prohibited control" (fun () ->
        let root = ClaimCore.TestSupport.RepositoryRoot.find ()
        let product = Path.Combine(root, "src/ClaimCore.Cli")

        let sources =
            Directory.EnumerateFiles(product, "*.fs", SearchOption.AllDirectories)
            |> Seq.filter (fun path -> Path.GetFileName path <> "CliMessages.fs")
            |> Seq.toList

        Expect.isNonEmpty sources "The complete CLI source set is checked"
        requireTyped sources

        let fixture =
            Path.Combine(root, "tests/ClaimCore.Tests/Architecture/NegativeFixtures.fs")

        Expect.equal (DirectCliMessageKey.Read()) "cli.fixture" "The negative source is compiled"

        Expect.contains
            (violations [ fixture ] |> List.map snd)
            "cli.fixture"
            "The same parser sees the compiled literal"

        Expect.throws
            (fun () -> requireTyped [ fixture ])
            "The isolated violation fails the production predicate")
