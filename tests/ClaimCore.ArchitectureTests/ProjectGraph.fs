module internal ClaimCore.ArchitectureTests.ProjectGraph

open System
open System.Reflection
open System.Runtime.CompilerServices

let check (contract: Manifest.Contract) (actual: Map<string, Set<string>>) =
    let expected = contract.Projects.Keys |> Set.ofSeq

    let named =
        actual
        |> Map.filter (fun name _ -> not (name.EndsWith("Tests", StringComparison.Ordinal)))

    let present = named.Keys |> Set.ofSeq

    if expected <> present then
        invalidOp (
            "projectGraph project set differs: "
            + string (Set.union (Set.difference expected present) (Set.difference present expected))
        )

    for KeyValue(name, references) in actual do
        if name.EndsWith("Tests", StringComparison.Ordinal) then
            let forbidden = Set.difference references expected

            if not (Set.isEmpty forbidden) then
                invalidOp (name + " projectGraph forbidden test references: " + string forbidden)
        elif references <> contract.Projects[name] then
            invalidOp (
                name
                + " projectGraph references differ: expected "
                + string contract.Projects[name]
                + "; actual "
                + string references
            )

let grant (contract: Manifest.Contract) tests name target =
    let permitted =
        Set.contains target tests
        || (contract.Projects |> Map.tryFind target |> Option.exists (Set.contains name))

    if not permitted then
        invalidOp (name + " internalsVisibleTo forbidden recipient " + target)

let internals (contract: Manifest.Contract) tests (assembly: Assembly) =
    let name = assembly.GetName().Name |> nonNull

    for attribute in assembly.GetCustomAttributes<InternalsVisibleToAttribute>() do
        let target = AssemblyName(attribute.AssemblyName).Name |> nonNull

        grant contract tests name target
