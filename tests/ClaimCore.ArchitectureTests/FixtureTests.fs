module ClaimCore.ArchitectureTests.FixtureTests

open System
open System.IO
open Expecto
open ArchUnitNET.Fluent
open ClaimCore.ArchitectureFixtures

let private architecture =
    lazy
        (Inspection.loadRequired AppContext.BaseDirectory [ "ClaimCore.ArchitectureFixtures" ]
         |> Inspection.build)

let private select suffix =
    ArchRuleDefinition
        .Types()
        .That()
        .HaveFullNameContaining("ClaimCore.ArchitectureFixtures." + suffix)

let private dependencyRule side shape =
    let model = architecture.Value
    let subjects = select (side + shape)
    let target = ArchRuleDefinition.Types().That().Are(typeof<Boundary>)
    Inspection.requireSelection model subjects |> ignore
    Inspection.requireSelection model target |> ignore
    subjects.Should().NotDependOnAny(target) :> IArchRule

let private pair shape =
    [
        testCase (shape + " accepts pure counterpart") (fun () ->
            Inspection.check architecture.Value (dependencyRule "Good" shape))
        testCase (shape + " detects forbidden dependency") (fun () ->
            let failures = Inspection.violations architecture.Value (dependencyRule "Bad" shape)
            Expect.isNonEmpty failures "The qualified F# form must retain the forbidden edge"

            Expect.isTrue
                (failures |> List.exists (fun text -> text.Contains("Boundary")))
                "Failure identifies the intended target, not merely an empty rule")
    ]

let private methodRule side shape =
    let subjects = select (side + shape)

    let methods =
        ArchRuleDefinition
            .MethodMembers()
            .That()
            .AreDeclaredIn(typeof<Boundary>)
            .And()
            .HaveNameContaining("Forbidden")

    Inspection.requireSelection architecture.Value subjects |> ignore
    Inspection.requireSelection architecture.Value methods |> ignore
    subjects.Should().NotCallAny(methods) :> IArchRule

let private preflight =
    [
        testCase "missing input fails before loading" (fun () ->
            Expect.throwsT<InvalidOperationException>
                (fun () ->
                    Inspection.requireFile AppContext.BaseDirectory "IntentionallyAbsent"
                    |> ignore)
                "Missing input cannot look like absence of violations")
        testCase "duplicate inputs are refused" (fun () ->
            Expect.throwsT<ArgumentException>
                (fun () ->
                    Inspection.loadRequired
                        AppContext.BaseDirectory
                        [ "ClaimCore.ArchitectureFixtures"; "ClaimCore.ArchitectureFixtures" ]
                    |> ignore)
                "Duplicated inputs cannot hide an omitted root")
        testCase "empty input set is refused" (fun () ->
            Expect.throwsT<ArgumentException>
                (fun () -> Inspection.loadRequired AppContext.BaseDirectory [] |> ignore)
                "No empty model")
        testCase "empty required selector is refused" (fun () ->
            Expect.throwsT<InvalidOperationException>
                (fun () ->
                    Inspection.requireSelection architecture.Value (select "IntentionallyAbsent")
                    |> ignore)
                "A renamed required target must fail")
        testCase "member rule permits other method on same type" (fun () ->
            Inspection.check architecture.Value (methodRule "Good" "Method"))
        testCase "member rule detects only forbidden method" (fun () ->
            let failures = Inspection.violations architecture.Value (methodRule "Bad" "Method")
            Expect.isNonEmpty failures "Forbidden call must be found"

            Expect.isTrue
                (failures |> List.exists (fun text -> text.Contains("Forbidden")))
                "Failure names the selected method")
        testCase "omitted reflected type fails completeness" (fun () ->
            Expect.throwsT<InvalidOperationException>
                (fun () ->
                    Inspection.requireTypeNames
                        "Synthetic"
                        [ "Synthetic.Present"; "Synthetic.Omitted" ]
                        [ "Synthetic.Present" ])
                "A nonempty but incomplete loaded model must fail")
        testCase "F# startup comma normalization preserves completeness" (fun () ->
            Inspection.requireTypeNames
                "Synthetic"
                [ "<StartupCode$Synthetic>.$.NETCoreApp\\,Version=v10.0.AssemblyAttributes" ]
                [ "<StartupCode$Synthetic>.$.NETCoreApp,Version=v10.0.AssemblyAttributes" ])
    ]

let private memberPair shape =
    [
        testCase (shape + " permits noncalling member counterpart") (fun () ->
            Inspection.check architecture.Value (methodRule "Good" shape))
        testCase (shape + " detects forbidden member call") (fun () ->
            let failures = Inspection.violations architecture.Value (methodRule "Bad" shape)
            Expect.isNonEmpty failures "The qualified F# form must retain the forbidden call"

            Expect.isTrue
                (failures |> List.exists (fun text -> text.Contains("Forbidden")))
                "Failure identifies the selected method")
    ]

let private platformPair shape (target: Type) methodName =
    let rule side =
        let subjects = select (side + shape)

        let methods =
            ArchRuleDefinition
                .MethodMembers()
                .That()
                .AreDeclaredIn(target)
                .And()
                .HaveNameContaining(methodName)

        Inspection.requireSelection architecture.Value subjects |> ignore
        Inspection.requireSelection architecture.Value methods |> ignore
        subjects.Should().NotCallAny(methods) :> IArchRule

    [
        testCase (shape + " permits value-only use") (fun () ->
            Inspection.check architecture.Value (rule "Good"))
        testCase (shape + " detects platform effect") (fun () ->
            let failures = Inspection.violations architecture.Value (rule "Bad")
            Expect.isNonEmpty failures "Platform effect must be detected"

            Expect.isTrue
                (failures |> List.exists (fun text -> text.Contains(methodName)))
                "Violation must identify the platform method")
    ]

let tests =
    let forms =
        [
            "Function"
            "Generic"
            "Closure"
            "Nested"
            "Task"
            "Async"
            "Sequence"
            "Signature"
            "Union"
            "Interface"
        ]
        |> List.collect pair

    let memberForms =
        [ "Function"; "Generic"; "Closure"; "Nested"; "Task"; "Async"; "Sequence" ]
        |> List.collect memberPair

    testList
        "F# inspection qualification"
        (forms
         @ memberForms
         @ preflight
         @ platformPair "Clock" typeof<DateTime> "get_UtcNow"
         @ platformPair "Io" typeof<File> "ReadAllText")
