module ClaimCore.TestSupport.TestGuards

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Text.Json
open System.Xml.Linq
open Expecto

let private publicStatic (value: MemberInfo) =
    match value with
    | :? PropertyInfo as property ->
        match property.GetGetMethod true with
        | null -> false
        | getter -> getter.IsPublic && getter.IsStatic
    | :? FieldInfo as field -> field.IsPublic && field.IsStatic
    | _ -> false

let private testMembers (subject: Type) =
    let flags =
        BindingFlags.Public
        ||| BindingFlags.NonPublic
        ||| BindingFlags.Static
        ||| BindingFlags.Instance
        ||| BindingFlags.DeclaredOnly

    let properties =
        subject.GetProperties flags
        |> Array.filter (fun value ->
            value.PropertyType = typeof<Test>
            && (value.IsDefined(typeof<TestsAttribute>, false) || publicStatic value))
        |> Array.map (fun value -> value :> MemberInfo)

    // Private fields are storage/helpers, not the runner's supported public field entrypoints.
    let fields =
        subject.GetFields flags
        |> Array.filter (fun value -> value.FieldType = typeof<Test> && value.IsPublic)
        |> Array.map (fun value -> value :> MemberInfo)

    Array.append properties fields

let undiscoveredTests (assembly: Assembly) =
    assembly.GetTypes()
    |> Array.collect testMembers
    |> Array.filter (fun value ->
        not (
            (value.DeclaringType |> nonNull).IsVisible
            && publicStatic value
            && value.IsDefined(typeof<TestsAttribute>, false)
        ))
    |> Array.map (fun value ->
        let name = (value.DeclaringType |> nonNull).FullName |> nonNull
        name + "." + value.Name)

let requiredAttribute name (item: XElement) =
    match item.Attribute(XName.Get name) with
    | null -> invalidOp ("Missing project attribute: " + name)
    | attribute -> attribute.Value

let private compileItems configuration project =
    let start =
        ProcessStartInfo("dotnet", RedirectStandardOutput = true, RedirectStandardError = true)

    for argument in
        [
            "msbuild"
            project
            "-getItem:Compile"
            "-property:Configuration=" + configuration
            "-nodeReuse:false"
        ] do
        start.ArgumentList.Add argument

    use child = new Process(StartInfo = start)

    if not (child.Start()) then
        invalidOp ("Could not evaluate " + project)

    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit 30000) then
        child.Kill true
        child.WaitForExit()
        invalidOp ("Compile item evaluation timed out: " + project)

    errors.GetAwaiter().GetResult() |> ignore

    if child.ExitCode <> 0 then
        invalidOp ("Compile item evaluation failed: " + project)

    output.GetAwaiter().GetResult()

let sourceMembership configuration (project: string) =
    let directory =
        Path.GetDirectoryName(Path.GetFullPath project)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "A project must have a parent directory.")

    use items = JsonDocument.Parse(compileItems configuration project)

    let compiled =
        items.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
        |> Seq.map (fun item ->
            item.GetProperty("FullPath").GetString()
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp ("Compile item has no path: " + project)))
        |> Set.ofSeq

    let sources =
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        |> Seq.filter (fun path ->
            Path.GetExtension path = ".fs" || Path.GetExtension path = ".fsi")
        |> Seq.map Path.GetFullPath
        |> Set.ofSeq

    let uncompiled = Set.difference sources compiled |> Set.toList
    let missing = compiled |> Set.filter (File.Exists >> not) |> Set.toList
    uncompiled, missing

let rec completeTests =
    function
    | TestCase(_, focus) -> focus = Normal
    | TestList(children, focus) -> focus = Normal && List.forall completeTests children
    | TestLabel(_, child, focus) -> focus = Normal && completeTests child
    | Test.Sequenced(_, child) -> completeTests child

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
    |> Seq.map (fun item -> Path.Combine(root, requiredAttribute "Path" item))

let private membership (assembly: Assembly) project =
    match assembly.GetCustomAttribute<AssemblyConfigurationAttribute>() with
    | null -> invalidOp "The test assembly must declare its build configuration."
    | attribute -> sourceMembership attribute.Configuration project

let tests (assembly: Assembly) =
    testList
        "test discovery guards"
        [
            testCase "every public test value carries the discovery attribute" (fun () ->
                let missing = undiscoveredTests (assembly)
                Expect.isEmpty missing ("Undiscovered tests: " + String.concat ", " missing))
            testCase "an unattributed test value is rejected" (fun () ->
                Expect.equal
                    (unattributedFixture () |> undiscoveredTests)
                    [| "Fixture.unregistered"; "Fixture.missing" |]
                    "The guard detects a value the runner would miss")
            testCase "annotated test properties and public fields must be discoverable" (fun () ->
                Expect.equal
                    (invisibleFixture () |> undiscoveredTests |> Set.ofArray)
                    (set
                        [
                            "HiddenFixture.annotatedProperty"
                            "HiddenFixture.annotatedField"
                            "VisibleFixture.privateProperty"
                        ])
                    "Hidden public values and private annotated getters refuse; private fields are storage, not public test entrypoints")
            testCase "every solution project's source is compiled and exists" (fun () ->
                for project in projects () do
                    let uncompiled, missing = membership assembly project

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

                    Expect.equal
                        (membership assembly project)
                        ([ stray ], [])
                        "Stray signature detected"))
            testCase "a missing compile item is rejected" (fun () ->
                withProject (fun directory project ->
                    let source = Path.Combine(directory, "Included.fs")
                    File.Delete source

                    Expect.equal
                        (membership assembly project)
                        ([], [ source ])
                        "Missing source detected"))
            testCase "a compile item excluded by a condition is rejected" (fun () ->
                withProject (fun directory project ->
                    File.WriteAllText(
                        project,
                        "<Project><ItemGroup><Compile Include=\"Included.fs\" Condition=\"false\" /></ItemGroup></Project>"
                    )

                    Expect.equal
                        (membership assembly project)
                        ([ Path.Combine(directory, "Included.fs") ], [])
                        "Presence in XML does not prove compilation"))
            testCase "focused and pending tests cannot satisfy a complete run" (fun () ->
                let discovered = Expecto.Impl.testFromAssembly (assembly)
                Expect.isSome discovered "The assembly contains discovered tests"

                Expect.isTrue
                    (discovered |> Option.exists completeTests)
                    "Focused or pending tests found")
            testCase "focused and pending nested fixtures are rejected" (fun () ->
                for fixture in [ ftestCase "focused" ignore; ptestCase "pending" ignore ] do
                    Expect.isFalse
                        (testList "nested" [ testSequenced fixture ] |> completeTests)
                        "Incomplete nested fixture detected")
        ]
