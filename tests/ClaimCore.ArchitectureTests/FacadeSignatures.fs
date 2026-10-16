module internal ClaimCore.ArchitectureTests.FacadeSignatures

open System
open System.Reflection
open System.Collections.Generic

let private members inspect (value: Type) =
    let flags =
        BindingFlags.Public
        ||| BindingFlags.Instance
        ||| BindingFlags.Static
        ||| BindingFlags.DeclaredOnly

    for method in value.GetMethods flags do
        inspect method.ReturnType

        method.GetParameters()
        |> Array.iter (fun parameter -> inspect parameter.ParameterType)

    for constructor in value.GetConstructors flags do
        constructor.GetParameters()
        |> Array.iter (fun parameter -> inspect parameter.ParameterType)

    for field in value.GetFields flags do
        inspect field.FieldType

let check (roots: Type list) =
    let visited = HashSet<Type>()

    let forbidden =
        set
            [
                "ClaimCore.Domain"
                "ClaimCore.Application"
                "ClaimCore.RecordFormat"
                "ClaimCore.Contracts"
                "ClaimCore.Postgres"
            ]

    let local =
        roots
        |> List.map (fun value -> value.Assembly.GetName().Name |> nonNull)
        |> Set.ofList

    let rec inspect (value: Type) =
        if visited.Add value then
            let name = value.Assembly.GetName().Name |> nonNull

            if Set.contains name forbidden then
                invalidOp ("Semantic facade type: " + value.FullName)

            if value.IsGenericType then
                value.GetGenericArguments() |> Array.iter inspect

            if value.HasElementType then
                value.GetElementType() |> nonNull |> inspect

            if Set.contains name local then
                members inspect value

    roots |> List.iter inspect
