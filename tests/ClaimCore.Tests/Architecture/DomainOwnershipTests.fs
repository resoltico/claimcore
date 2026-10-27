module ClaimCore.Tests.Architecture.DomainOwnershipTests

open Expecto
open ClaimCore.Tests.Architecture.CompilerModel

let private constraintExemptions =
    [
        "ScalarRule"
        "CalendarDateScalarRule"
        "AmountScalarRule"
        "CurrencyScalarRule"
        "TextFormatConstraints"
        "ScalarTextConstraints"
        "CaseStatusScalarRule"
        "MoneyDescriptor"
    ]
    |> List.map (fun name ->
        "type:ClaimCore.Domain." + name, "Domain-owned constraint constants published to renderers")
    |> Map.ofList

let private controls () =
    let model = fixtures.Value

    let root name =
        model.Bindings
        |> List.filter (fun binding ->
            binding.Symbol.FullName =
                "ClaimCore.Tests.Architecture.OwnershipFixtures." + name + ".read")
        |> List.map _.Key

    let core = root "Core"
    let presentation = root "Presentation"
    Expect.hasLength core 1 "The compiled core control is present"
    Expect.hasLength presentation 1 "The compiled presentation control is present"
    let reached = reachable model.Edges core
    let used = "type:ClaimCore.Tests.Architecture.OwnershipFixtures.Used"
    let unused = "type:ClaimCore.Tests.Architecture.OwnershipFixtures.PresentationOnly"

    for name in [ "CoreDetail"; "DetailAlias" ] do
        Expect.isTrue
            (Set.contains ("type:ClaimCore.Tests.Architecture.OwnershipFixtures." + name) reached)
            "Later body expressions and their authored type aliases remain reachable"

    Expect.isTrue (Set.contains used reached) "A called member reaches its data declaration"

    Expect.isFalse
        (Set.contains unused reached)
        "An unused member of the same module does not make its declaration core-owned"

    Expect.throws
        (fun () -> Expect.isTrue (Set.contains unused reached) "Ownership violation")
        "The isolated compiled violation fails the same ownership predicate"

    Expect.isTrue
        (reachable model.Edges (core @ presentation) |> Set.contains unused)
        "Adding an actual consumer makes the declaration reachable"

let private ownership () =
    controls ()
    let model = domain.Value

    let roots =
        core.Value
        |> List.collect (fun source ->
            source.Uses
            |> Array.filter (fun usage -> not usage.IsFromDefinition)
            |> Array.collect (useKeys >> List.toArray)
            |> Array.toList)

    let reached = reachable model.Edges roots

    let publicTypes =
        model.Entities
        |> List.filter (fun entity -> not entity.IsFSharpModule && entity.Accessibility.IsPublic)
        |> List.map entityKey
        |> Set.ofList

    for KeyValue(key, reason) in constraintExemptions do
        Expect.isTrue
            (Set.contains key publicTypes)
            ("Exemption identifies a declared constraint: " + key)

        Expect.isNonEmpty reason "Every exemption explains domain ownership"

    let violations =
        Set.difference publicTypes reached
        |> Set.filter (fun name -> not (Map.containsKey name constraintExemptions))
        |> Set.toList

    Expect.isEmpty
        violations
        ("Domain declarations require core consumers: " + String.concat ", " violations)

[<Tests>]
let tests =
    testCase
        "Domain declarations are owned by core consumers, including mixed-module controls"
        ownership
