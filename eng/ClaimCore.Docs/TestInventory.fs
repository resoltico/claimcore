namespace ClaimCore.Docs

open System
open System.IO
open System.Reflection
open System.Text.Json

type private TestInventoryResourceAnchor = class end

[<RequireQualifiedAccess>]
module TestInventory =
    let private prefix = "ClaimCore.TestInventory."

    let private expectedAssemblies =
        set
            [
                "ClaimCore.AcceptanceTests"
                "ClaimCore.ArchitectureTests"
                "ClaimCore.ConcurrencyQualificationTests"
                "ClaimCore.DocsTests"
                "ClaimCore.IntegrationTests"
                "ClaimCore.MigrationQualificationTests"
                "ClaimCore.RecoveryQualificationTests"
                "ClaimCore.Tests"
                "ClaimCore.WebTests"
            ]

    let assemblies = expectedAssemblies |> Set.toList

    let private exactProperties expected (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList

            names.Length = (names |> Set.ofList |> Set.count)
            && Set.ofList names = Set.ofList expected

    let private text (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)

        if value.ValueKind <> JsonValueKind.String then
            invalidOp $"Test inventory property '{name}' must be text."

        value.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Test inventory text must not be null.")

    let private readResource (assembly: Assembly) resource =
        match assembly.GetManifestResourceStream(resource) |> Option.ofObj with
        | None -> invalidOp "A compiled test-inventory resource disappeared."
        | Some stream ->
            use source = stream
            use document = JsonDocument.Parse(source)
            let root = document.RootElement
            let mutable version = 0

            if
                not (exactProperties [ "schemaVersion"; "assembly"; "tests" ] root)
                || not (root.GetProperty("schemaVersion").TryGetInt32(&version))
                || version <> 1
                || root.GetProperty("tests").ValueKind <> JsonValueKind.Array
            then
                invalidOp "A compiled test inventory has an invalid shape."

            let assemblyName = text "assembly" root

            let names =
                root.GetProperty("tests").EnumerateArray()
                |> Seq.map (fun value ->
                    if value.ValueKind <> JsonValueKind.String then
                        invalidOp "Test inventory entries must be text."

                    value.GetString()
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> invalidOp "Test inventory entry is null."))
                |> Seq.toList

            let ordered =
                names
                |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))

            if
                names.IsEmpty
                || names <> ordered
                || names.Length <> (names |> Set.ofList |> Set.count)
                || names
                   |> List.exists (fun name ->
                       String.IsNullOrWhiteSpace(name)
                       || name.Length > 500
                       || name |> Seq.exists Char.IsControl)
            then
                invalidOp
                    $"Test inventory '{assemblyName}' names must be nonempty, unique, bounded, and ordinally sorted."

            assemblyName, Set.ofList names

    let private inventories =
        lazy
            (let assembly = typeof<TestInventoryResourceAnchor>.Assembly

             let loaded =
                 assembly.GetManifestResourceNames()
                 |> Array.filter (fun name -> name.StartsWith(prefix, StringComparison.Ordinal))
                 |> Array.map (readResource assembly)
                 |> Array.toList

             let names = loaded |> List.map fst

             if names.Length <> (names |> Set.ofList |> Set.count) then
                 invalidOp "Compiled test inventories contain duplicate assemblies."

             if Set.ofList names <> expectedAssemblies then
                 invalidOp "Compiled test inventories do not cover the exact required assemblies."

             Map.ofList loaded)

    let names assembly =
        inventories.Value
        |> Map.tryFind assembly
        |> Option.defaultWith (fun () -> invalidOp $"Test assembly '{assembly}' is not registered.")
