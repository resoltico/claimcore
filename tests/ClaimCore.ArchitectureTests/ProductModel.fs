module ClaimCore.ArchitectureTests.ProductModel

open System
open ArchUnitNET.Fluent

let assemblies =
    lazy (Inspection.loadRequired AppContext.BaseDirectory ProductPolicy.names)

let architecture = lazy (Inspection.build assemblies.Value)

let select part =
    let name = ProductPolicy.name part

    let assembly =
        assemblies.Value |> Array.find (fun item -> item.GetName().Name = name)

    ArchRuleDefinition.Types().That().ResideInAssembly(assembly)
