module ClaimCore.ArchitectureTests.EffectPolicy

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Threading
open ArchUnitNET.Fluent
open ArchUnitNET.Fluent.Syntax.Elements.Types

/// Select effects rather than whole namespaces: hashing and value-only cryptography remain valid.
let forbiddenTypes =
    [
        typeof<Console>
        typeof<File>
        typeof<Directory>
        typeof<Environment>
        typeof<Random>
        typeof<RandomNumberGenerator>
        typeof<Thread>
    ]

let clockCalls =
    [
        typeof<DateTime>, "get_Now"
        typeof<DateTime>, "get_UtcNow"
        typeof<DateTime>, "get_Today"
        typeof<DateTimeOffset>, "get_Now"
        typeof<DateTimeOffset>, "get_UtcNow"
        typeof<TimeProvider>, "GetUtcNow"
    ]

let forbiddenCalls =
    clockCalls
    @ [
        typeof<TimeZoneInfo>, "get_Local"
        typeof<Guid>, "NewGuid"
        typeof<Guid>, "CreateVersion7"
        typeof<Stopwatch>, "GetTimestamp"
        typeof<Stopwatch>, "Start"
        typeof<TimeProvider>, "GetTimestamp"
        typeof<TimeProvider>, "GetLocalNow"
    ]

let methodSelection (target: Type) methodName =
    ArchRuleDefinition
        .MethodMembers()
        .That()
        .AreDeclaredIn(target)
        .And()
        .HaveNameContaining(methodName)

/// Product inspection and qualification fixtures exercise the same policy, not parallel copies.
let violations model (subjects: GivenTypesConjunction) =
    Inspection.requireSelection model subjects |> ignore

    [
        for target in forbiddenTypes do
            let forbidden = ArchRuleDefinition.Types().That().Are(target)
            Inspection.requireSelection model forbidden |> ignore
            yield! Inspection.violations model (subjects.Should().NotDependOnAny(forbidden))

        for target, methodName in forbiddenCalls do
            let forbidden = methodSelection target methodName
            Inspection.requireSelection model forbidden |> ignore
            yield! Inspection.violations model (subjects.Should().NotCallAny(forbidden))
    ]
