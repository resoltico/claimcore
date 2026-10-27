module ClaimCore.Tests.BuildIdentityTests

open System
open System.Reflection
open System.Reflection.Emit
open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol

let private attributes (identity: BuildIdentity) =
    [
        typeof<AssemblyProductAttribute>, identity.Product
        typeof<AssemblyInformationalVersionAttribute>, identity.Version
        typeof<AssemblyFileVersionAttribute>, identity.FileVersion
    ]

let private assembly (version: string) (values: (Type * string) list) =
    let name = AssemblyName("ReleaseIdentityControl" + Guid.NewGuid().ToString("N"))
    name.Version <- Version.Parse version

    let emitted =
        AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.RunAndCollect)

    for kind, value in values do
        let constructor =
            kind.GetConstructor [| typeof<string> |]
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failtest "Control attribute constructor")

        emitted.SetCustomAttribute(CustomAttributeBuilder(constructor, [| box value |]))

    emitted

let private identity () =
    let expected = BuildIdentity.current
    let same = assembly expected.AssemblyVersion (attributes expected)
    BuildIdentity.requireCompatibleAssembly same

    for changed in
        [
            { expected with
                Product = expected.Product + " other"
            }
            { expected with
                Version = expected.Version + "-other"
            }
            { expected with
                FileVersion = "0.0.0.99"
            }
            { expected with
                AssemblyVersion = "0.0.0.99"
            }
        ] do
        let other = assembly changed.AssemblyVersion (attributes changed)

        Expect.throwsT<InvalidOperationException>
            (fun () -> BuildIdentity.requireCompatibleAssembly other)
            "Every release identity component participates"

let private missingOrBlank () =
    let expected = BuildIdentity.current
    let values = attributes expected

    for kind, _ in values do
        let missing =
            assembly expected.AssemblyVersion (values |> List.filter (fst >> (<>) kind))

        Expect.throwsT<InvalidOperationException>
            (fun () -> BuildIdentity.requireCompatibleAssembly missing)
            "Missing metadata refuses"

        let blank =
            assembly
                expected.AssemblyVersion
                (values |> List.map (fun (key, value) -> key, if key = kind then " " else value))

        Expect.throwsT<InvalidOperationException>
            (fun () -> BuildIdentity.requireCompatibleAssembly blank)
            "Blank metadata refuses"

let private duplicates () =
    let expected = BuildIdentity.current

    let duplicate =
        assembly
            expected.AssemblyVersion
            (attributes expected @ [ typeof<AssemblyProductAttribute>, expected.Product ])

    Expect.equal
        (duplicate.GetCustomAttributes(typeof<AssemblyProductAttribute>, false).Length)
        2
        "The real control contains duplicate metadata"

    Expect.throwsT<InvalidOperationException>
        (fun () -> BuildIdentity.requireCompatibleAssembly duplicate)
        "Duplicated metadata cannot be selected arbitrarily"

[<Tests>]
let tests =
    testList
        "release identity admission"
        [
            testCase "matching assemblies admit and mixed release components refuse" identity
            testCase "missing and blank metadata refuse" missingOrBlank
            testCase "duplicate metadata refuses" duplicates
        ]
