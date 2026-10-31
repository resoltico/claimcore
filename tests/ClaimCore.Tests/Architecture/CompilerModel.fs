module internal ClaimCore.Tests.Architecture.CompilerModel

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

let private checker = lazy (FSharpChecker.Create(keepAssemblyContents = true))

let entityKey (entity: FSharpEntity) =
    let name =
        entity.TryFullName
        |> Option.defaultWith (fun () -> entity.AccessPath + "." + entity.CompiledName)

    (if entity.IsFSharpModule then "module:" else "type:") + name

let private memberKey (value: FSharpMemberOrFunctionOrValue) =
    value.DeclaringEntity
    |> Option.map (fun owner -> entityKey owner + "/" + value.CompiledName)

let rec private typeKeys (value: FSharpType) =
    if value.IsGenericParameter then
        []
    else
        [
            if value.HasTypeDefinition then
                yield entityKey value.TypeDefinition
            for argument in value.GenericArguments do
                yield! typeKeys argument
            if value.IsAbbreviation then
                yield! typeKeys value.AbbreviatedType
        ]

let private symbolKeys (symbol: FSharpSymbol) =
    match symbol with
    | :? FSharpEntity as entity -> [ entityKey entity ]
    | :? FSharpUnionCase as case -> [ entityKey case.DeclaringEntity ]
    | :? FSharpField as field -> field.DeclaringEntity |> Option.map entityKey |> Option.toList
    | :? FSharpMemberOrFunctionOrValue as value when value.IsModuleValueOrMember ->
        [
            yield! memberKey value |> Option.toList
            yield! value.DeclaringEntity |> Option.map entityKey |> Option.toList
            yield! typeKeys value.FullType
        ]
    | _ -> []

let useKeys (usage: FSharpSymbolUse) =
    symbolKeys usage.Symbol
    @ (usage.GenericArguments |> List.collect (snd >> typeKeys))

[<NoEquality; NoComparison>]
type Binding =
    {
        Symbol: FSharpMemberOrFunctionOrValue
        Key: string
        Body: FSharpExpr
        Range: range
    }

[<NoEquality; NoComparison>]
type Model =
    {
        Entities: FSharpEntity list
        Bindings: Binding list
        Uses: FSharpSymbolUse array
        Edges: Map<string, Set<string>>
    }

let rec private expressionRanges (expression: FSharpExpr) =
    seq {
        yield expression.Range

        for child in expression.ImmediateSubExpressions do
            yield! expressionRanges child
    }

let private declarations files =
    let entities = ResizeArray<FSharpEntity>()
    let bindings = ResizeArray<Binding>()

    let rec visit declarations =
        for declaration in declarations do
            match declaration with
            | FSharpImplementationFileDeclaration.Entity(entity, children) ->
                if not entity.IsNamespace then
                    entities.Add entity

                visit children
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(value, _, body) ->
                if not value.IsCompilerGenerated then
                    memberKey value
                    |> Option.iter (fun key ->
                        let bodyRange = body.Range

                        let declaration =
                            value.ImplementationLocation
                            |> Option.defaultValue value.DeclarationLocation

                        let span =
                            if declaration.FileName = bodyRange.FileName then
                                let ending =
                                    expressionRanges body
                                    |> Seq.filter (fun span ->
                                        span.FileName = declaration.FileName)
                                    |> Seq.map (fun span -> span.End)
                                    |> Seq.maxBy (fun position -> position.Line, position.Column)

                                Range.mkRange declaration.FileName declaration.Start ending
                            else
                                bodyRange

                        bindings.Add
                            {
                                Symbol = value
                                Key = key
                                Body = body
                                Range = span
                            })
            | FSharpImplementationFileDeclaration.InitAction _ -> ()

    for file: FSharpImplementationFileContents in files do
        visit file.Declarations

    List.ofSeq entities, List.ofSeq bindings

let private contains (outer: range) (inner: range) =
    outer.FileName = inner.FileName
    && (outer.StartLine, outer.StartColumn) <= (inner.StartLine, inner.StartColumn)
    && (inner.EndLine, inner.EndColumn) <= (outer.EndLine, outer.EndColumn)

let usesIn (model: Model) (binding: Binding) =
    model.Uses
    |> Array.filter (fun usage -> not usage.IsFromDefinition && contains binding.Range usage.Range)

let private edges
    (entities: FSharpEntity list)
    (bindings: Binding list)
    (uses: FSharpSymbolUse array)
    =
    let data =
        entities
        |> List.map (fun (entity: FSharpEntity) ->
            let fields =
                if entity.IsFSharpModule then
                    []
                else
                    [
                        yield!
                            entity.FSharpFields
                            |> Seq.collect (fun field -> typeKeys field.FieldType)
                        for case in entity.UnionCases do
                            yield!
                                case.Fields |> Seq.collect (fun field -> typeKeys field.FieldType)
                        if entity.IsFSharpAbbreviation then
                            yield! typeKeys entity.AbbreviatedType
                    ]

            entityKey entity, Set.ofList fields)

    let functions =
        bindings
        |> List.map (fun binding ->
            let references =
                uses
                |> Array.filter (fun usage ->
                    not usage.IsFromDefinition && contains binding.Range usage.Range)
                |> Array.collect (useKeys >> List.toArray)
                |> Set.ofArray

            binding.Key, Set.union references (typeKeys binding.Symbol.FullType |> Set.ofList))

    data @ functions
    |> List.groupBy fst
    |> List.map (fun (key, values) -> key, values |> List.map snd |> Set.unionMany)
    |> Map.ofList

let private arguments project =
    let directory = Directory.CreateTempSubdirectory("claimcore-compiler-options-")

    try
        let start = ProcessStartInfo("dotnet", UseShellExecute = false)
        start.WorkingDirectory <- ClaimCore.TestSupport.RepositoryRoot.find ()
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true

        for argument in
            [
                "msbuild"
                project
                "-nologo"
                "-t:Compile"
                "-p:Configuration=Debug"
                "-p:DesignTimeBuild=true"
                "-p:BuildProjectReferences=false"
                "-p:SkipCompilerExecution=true"
                "-p:ProvideCommandLineArgs=true"
                "-p:NonExistentFile=" + Path.Combine(directory.FullName, "force-options")
                "-getItem:FscCommandLineArgs"
            ] do
            start.ArgumentList.Add argument

        let result = ClaimCore.TestSupport.BoundedProcess.run start None 4_194_304 30_000

        if result.ExitCode <> 0 then
            invalidOp (
                "Compiler option evaluation failed: "
                + Text.Encoding.UTF8.GetString result.StandardError
            )

        use document = JsonDocument.Parse(ReadOnlyMemory<byte> result.StandardOutput)

        let options =
            document.RootElement
                .GetProperty("Items")
                .GetProperty("FscCommandLineArgs")
                .EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("Identity").GetString() |> nonNull)
            |> Seq.map (fun argument ->
                if
                    (argument.EndsWith(".fs", StringComparison.Ordinal)
                     || argument.EndsWith(".fsi", StringComparison.Ordinal))
                    && not (argument.StartsWith("-", StringComparison.Ordinal))
                    && not (Path.IsPathRooted argument)
                then
                    Path.Combine(Path.GetDirectoryName project |> nonNull, argument)
                else
                    argument)
            |> Seq.toArray

        if options.Length = 0 then
            invalidOp "Compiler options must be nonempty."

        options
    finally
        directory.Delete true

let load relative =
    let project = Path.Combine(ClaimCore.TestSupport.RepositoryRoot.find (), relative)

    let options =
        checker.Value.GetProjectOptionsFromCommandLineArgs(project, arguments project)

    let result =
        checker.Value.ParseAndCheckProject options
        |> fun work -> Async.RunSynchronously(work, 60_000)

    let errors =
        result.Diagnostics
        |> Array.filter (fun problem -> problem.Severity = FSharpDiagnosticSeverity.Error)

    if errors.Length <> 0 then
        errors |> Array.map string |> String.concat Environment.NewLine |> invalidOp

    let entities, bindings = declarations result.AssemblyContents.ImplementationFiles
    let uses = result.GetAllUsesOfAllSymbols()

    if entities.IsEmpty || uses.Length = 0 then
        invalidOp "Compiler analysis must contain declarations and uses."

    {
        Entities = entities
        Bindings = bindings
        Uses = uses
        Edges = edges entities bindings uses
    }

let reachable edges roots =
    let pending = Collections.Generic.Queue<string>(roots: seq<string>)
    let visited = Collections.Generic.HashSet<string>()

    while pending.Count <> 0 do
        let key = pending.Dequeue()

        if visited.Add key then
            for target in Map.tryFind key edges |> Option.defaultValue Set.empty do
                pending.Enqueue target

    Set.ofSeq visited

let domain = lazy (load "src/ClaimCore.Domain/ClaimCore.Domain.fsproj")
let fixtures = lazy (load "tests/ClaimCore.Tests/ClaimCore.Tests.fsproj")

let core =
    lazy
        ([
            "ClaimCore.Application.Contracts"
            "ClaimCore.Application"
            "ClaimCore.RecordFormat"
            "ClaimCore.Postgres"
         ]
         |> List.map (fun name -> load ("src/" + name + "/" + name + ".fsproj")))
