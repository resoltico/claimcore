module ClaimCore.Tests.Architecture.ProductDeclarationTests

open Expecto
open FSharp.Compiler.Symbols
open ClaimCore.Tests.Architecture.CompilerModel
open ClaimCore.Tests.Architecture.ProductModel

// Every exception identifies a concrete declaration or case and its verified consumer/lifetime.
let private typeExemptions: Map<string, string> = Map.empty
let private caseExemptions: Map<string, string> = Map.empty

let private referencedTypes models =
    models
    |> List.collect (fun model ->
        model.Edges
        |> Map.toList
        |> List.collect (fun (origin, targets) ->
            targets |> Set.toList |> List.map owner |> List.filter ((<>) (owner origin))))
    |> Set.ofList

let private entryPoint (models: Model list) key =
    models
    |> List.exists (fun model ->
        model.Bindings
        |> List.exists (fun binding ->
            owner binding.Key = key
            && (binding.Symbol.Attributes
                |> Seq.exists (fun attribute ->
                    attribute.AttributeType.TryFullName =
                        Some "Microsoft.FSharp.Core.EntryPointAttribute"))))

let private unreferenced models =
    let references = referencedTypes models

    models
    |> List.collect _.Entities
    |> List.map entityKey
    |> List.distinct
    |> List.filter (fun key -> not (Set.contains key references) && not (entryPoint models key))

let private vocabularies (model: Model) =
    let rec calls (expression: FSharpExpr) =
        let location = expression.Range

        seq {
            match expression with
            | FSharp.Compiler.Symbols.FSharpExprPatterns.Call(_,
                                                              value,
                                                              enclosingArguments,
                                                              methodArguments,
                                                              _) when
                value.FullName = "ClaimCore.Domain.Vocabulary.ofUnion"
                ->
                let concrete =
                    (enclosingArguments @ methodArguments)
                    |> List.filter (fun argument -> argument.HasTypeDefinition)
                    |> List.map _.TypeDefinition
                    |> List.filter _.IsFSharpUnion
                    |> List.distinctBy entityKey

                match concrete with
                | [ entity ] -> yield entity
                | _ ->
                    invalidOp (
                        "An ofUnion call requires one resolved union: "
                        + location.FileName
                        + ":"
                        + string location
                        + " / "
                        + sprintf "%A" (enclosingArguments, methodArguments)
                    )
            | _ -> ()

            for child in expression.ImmediateSubExpressions do
                yield! calls child
        }

    model.Bindings
    |> List.collect (fun binding ->
        calls binding.Body
        |> Seq.map (fun entity -> entity, owner binding.Key)
        |> Seq.toList)
    |> List.distinctBy (fun (entity, container) -> entityKey entity, container)

let private usedCases (models: Model list) (entity: FSharpEntity) vocabularyOwner =
    models
    |> List.collect (fun model ->
        model.Bindings
        |> List.filter (fun binding -> owner binding.Key <> vocabularyOwner)
        |> List.collect (fun binding ->
            usesIn model binding
            |> Array.choose (fun usage ->
                match usage.Symbol with
                | :? FSharpUnionCase as case when
                    entityKey case.DeclaringEntity = entityKey entity
                    ->
                    Some case.Name
                | _ -> None)
            |> Array.toList))
    |> Set.ofList

let private unusedCases models =
    models
    |> List.collect vocabularies
    |> List.distinctBy (fun (entity, container) -> entityKey entity, container)
    |> List.collect (fun (entity, container) ->
        let used = usedCases models entity container

        entity.UnionCases
        |> Seq.choose (fun case ->
            if Set.contains case.Name used then
                None
            else
                Some(entityKey entity + "." + case.Name))
        |> Seq.toList)

let private explain exemptions declarations =
    for KeyValue(key, reason) in exemptions do
        Expect.isTrue
            (Set.contains key declarations)
            ("Exemption names a current declaration: " + key)

        Expect.isNonEmpty reason "An exception requires a verified reason"

let private requireComplete exemptions violations =
    let remaining =
        violations |> List.filter (fun key -> not (Map.containsKey key exemptions))

    Expect.isEmpty
        remaining
        ("Declarations without product consumers: " + String.concat ", " remaining)

let private controls () =
    let source = fixtures.Value
    let namespaceName = "ClaimCore.Tests.Architecture.DeclarationFixtures."

    let selected (key: string) =
        key.Contains(namespaceName, System.StringComparison.Ordinal)

    let model =
        { source with
            Entities = source.Entities |> List.filter (entityKey >> selected)
            Bindings = source.Bindings |> List.filter (fun binding -> selected binding.Key)
            Edges = source.Edges |> Map.filter (fun key _ -> selected key)
        }

    let prefix = "type:ClaimCore.Tests.Architecture.DeclarationFixtures."
    let absent = unreferenced [ model ]
    Expect.contains absent (prefix + "Unreferenced") "The compiled dead-type control is discovered"

    Expect.isFalse
        (List.contains (prefix + "Referenced") absent)
        "Another type consumes the positive control"

    Expect.throws
        (fun () -> requireComplete Map.empty [ prefix + "Unreferenced" ])
        "The same gate rejects the isolated dead type"

    let missing = unusedCases [ model ]

    Expect.contains
        missing
        (prefix + "CaseSample.Unused")
        "A reflected-only union case is not a product use"

    Expect.isFalse
        (List.contains (prefix + "CaseSample.Used") missing)
        "An explicit match outside the vocabulary owner counts as use"

    Expect.throws
        (fun () -> requireComplete Map.empty [ prefix + "CaseSample.Unused" ])
        "The same gate rejects the isolated unused case"

let private declarations () =
    controls ()
    let models = products.Value
    let types = models |> List.collect _.Entities |> List.map entityKey |> Set.ofList

    let cases =
        models
        |> List.collect vocabularies
        |> List.collect (fun (entity, _) ->
            entity.UnionCases
            |> Seq.map (fun case -> entityKey entity + "." + case.Name)
            |> Seq.toList)
        |> Set.ofList

    explain typeExemptions types
    explain caseExemptions cases
    requireComplete typeExemptions (unreferenced models)
    requireComplete caseExemptions (unusedCases models)

[<Tests>]
let tests =
    testCase "product declarations and vocabulary cases have actual consumers" declarations
