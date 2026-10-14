module ClaimCore.TestSupport.TestGuardTests

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Xml.Linq
open Expecto
open ClaimCore.TestSupport

let private unattributedFixture () =
    let assembly =
        AssemblyBuilder.DefineDynamicAssembly(
            AssemblyName("UnattributedTestFixture"),
            AssemblyBuilderAccess.RunAndCollect
        )

    let subject =
        assembly.DefineDynamicModule("Fixture").DefineType("Fixture", TypeAttributes.Public)

    let getter =
        subject.DefineMethod(
            "get_unregistered",
            MethodAttributes.Public
            ||| MethodAttributes.Static
            ||| MethodAttributes.SpecialName,
            typeof<Test>,
            Type.EmptyTypes
        )

    let body = getter.GetILGenerator()
    body.Emit OpCodes.Ldnull
    body.Emit OpCodes.Ret

    subject
        .DefineProperty("unregistered", PropertyAttributes.None, typeof<Test>, Type.EmptyTypes)
        .SetGetMethod
        getter

    subject.DefineField("missing", typeof<Test>, FieldAttributes.Public ||| FieldAttributes.Static)
    |> ignore

    subject.CreateType() |> ignore
    assembly :> Assembly

let private emittedProperty (subject: TypeBuilder) name visibility attribute =
    let getter =
        subject.DefineMethod(
            "get_" + name,
            visibility ||| MethodAttributes.Static ||| MethodAttributes.SpecialName,
            typeof<Test>,
            Type.EmptyTypes
        )

    let body = getter.GetILGenerator()

    body.Emit OpCodes.Ldnull
    body.Emit OpCodes.Ret

    let property =
        subject.DefineProperty(name, PropertyAttributes.None, typeof<Test>, Type.EmptyTypes)

    property.SetGetMethod getter
    attribute |> Option.iter property.SetCustomAttribute

let private emittedField (subject: TypeBuilder) name visibility attribute =
    let field =
        subject.DefineField(name, typeof<Test>, visibility ||| FieldAttributes.Static)

    attribute |> Option.iter field.SetCustomAttribute
    field

let private invisibleFixture () =
    let assembly =
        AssemblyBuilder.DefineDynamicAssembly(
            AssemblyName("InvisibleTestFixture"),
            AssemblyBuilderAccess.RunAndCollect
        )

    let code = assembly.DefineDynamicModule("Fixture")

    let attribute =
        CustomAttributeBuilder(
            typeof<TestsAttribute>.GetConstructor(Type.EmptyTypes) |> nonNull,
            [||]
        )

    let hidden = code.DefineType("HiddenFixture", TypeAttributes.NotPublic)
    emittedProperty hidden "annotatedProperty" MethodAttributes.Public (Some attribute)

    emittedField hidden "annotatedField" FieldAttributes.Public (Some attribute)
    |> ignore

    hidden.CreateType() |> ignore
    let visible = code.DefineType("VisibleFixture", TypeAttributes.Public)
    emittedProperty visible "privateProperty" MethodAttributes.Private (Some attribute)

    emittedField visible "privateStorage" FieldAttributes.Private (Some attribute)
    |> ignore

    emittedProperty visible "helper" MethodAttributes.Private None
    emittedField visible "backingField" FieldAttributes.Private None |> ignore
    emittedProperty visible "discoveredProperty" MethodAttributes.Public (Some attribute)

    emittedField visible "discoveredField" FieldAttributes.Public (Some attribute)
    |> ignore

    visible.CreateType() |> ignore
    assembly :> Assembly

let private withProject action =
    let directory =
        Path.Combine(Path.GetTempPath(), "claimcore-source-guard-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory directory |> ignore

    try
        let project = Path.Combine(directory, "Fixture.fsproj")

        File.WriteAllText(
            project,
            "<Project><ItemGroup><Compile Include=\"Included.fs\" /></ItemGroup></Project>"
        )

        File.WriteAllText(Path.Combine(directory, "Included.fs"), "module Included")
        action directory project
    finally
        Directory.Delete(directory, true)

let private projects () =
    let root = RepositoryRoot.find ()

    XDocument.Load(Path.Combine(root, "ClaimCore.slnx")).Descendants(XName.Get "Project")
    |> Seq.map (fun item -> Path.Combine(root, TestGuards.requiredAttribute "Path" item))

let private membership project =
    match Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>() with
    | null -> invalidOp "The test assembly must declare its build configuration."
    | attribute -> TestGuards.sourceMembership attribute.Configuration project

[<Tests>]
let tests =
    testList
        "test discovery guards"
        [
            testCase "every public test value carries the discovery attribute" (fun () ->
                let missing = TestGuards.undiscoveredTests (Assembly.GetExecutingAssembly())
                Expect.isEmpty missing ("Undiscovered tests: " + String.concat ", " missing))
            testCase "an unattributed test value is rejected" (fun () ->
                Expect.equal
                    (unattributedFixture () |> TestGuards.undiscoveredTests)
                    [| "Fixture.unregistered"; "Fixture.missing" |]
                    "The guard detects a value the runner would miss")
            testCase "annotated test properties and public fields must be discoverable" (fun () ->
                Expect.equal
                    (invisibleFixture () |> TestGuards.undiscoveredTests |> Set.ofArray)
                    (set
                        [
                            "HiddenFixture.annotatedProperty"
                            "HiddenFixture.annotatedField"
                            "VisibleFixture.privateProperty"
                        ])
                    "Hidden public values and private annotated getters refuse; private fields are storage, not public test entrypoints")
            testCase "every solution project's source is compiled and exists" (fun () ->
                for project in projects () do
                    let uncompiled, missing = membership project

                    Expect.isEmpty
                        uncompiled
                        (project + " has uncompiled sources: " + String.concat ", " uncompiled)

                    Expect.isEmpty
                        missing
                        (project + " has missing sources: " + String.concat ", " missing))
            testCase "a stray source file is rejected" (fun () ->
                withProject (fun directory project ->
                    let stray = Path.Combine(directory, "Stray.fsi")
                    File.WriteAllText(stray, "module Stray")

                    Expect.equal (membership project) ([ stray ], []) "Stray signature detected"))
            testCase "a missing compile item is rejected" (fun () ->
                withProject (fun directory project ->
                    let source = Path.Combine(directory, "Included.fs")
                    File.Delete source

                    Expect.equal (membership project) ([], [ source ]) "Missing source detected"))
            testCase "a compile item excluded by a condition is rejected" (fun () ->
                withProject (fun directory project ->
                    File.WriteAllText(
                        project,
                        "<Project><ItemGroup><Compile Include=\"Included.fs\" Condition=\"false\" /></ItemGroup></Project>"
                    )

                    Expect.equal
                        (membership project)
                        ([ Path.Combine(directory, "Included.fs") ], [])
                        "Presence in XML does not prove compilation"))
            testCase "focused and pending tests cannot satisfy a complete run" (fun () ->
                let discovered = Expecto.Impl.testFromAssembly (Assembly.GetExecutingAssembly())
                Expect.isSome discovered "The assembly contains discovered tests"

                Expect.isTrue
                    (discovered |> Option.exists TestGuards.completeTests)
                    "Focused or pending tests found")
            testCase "focused and pending nested fixtures are rejected" (fun () ->
                for fixture in [ ftestCase "focused" ignore; ptestCase "pending" ignore ] do
                    Expect.isFalse
                        (testList "nested" [ testSequenced fixture ] |> TestGuards.completeTests)
                        "Incomplete nested fixture detected")
        ]
