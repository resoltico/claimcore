module internal ClaimCore.Tests.Architecture.ApiRules

open System.Reflection
open ArchUnitNET.Domain
open ArchUnitNET.Fluent
open type ArchUnitNET.Fluent.ArchRuleDefinition

let private fileTypes =
    Types()
        .That()
        .HaveFullName("System.IO.File")
        .Or()
        .HaveFullName("System.IO.Directory")
        .Or()
        .HaveFullName("System.IO.FileStream")
        .Or()
        .HaveFullName("System.IO.FileInfo")
        .Or()
        .HaveFullName("System.IO.DirectoryInfo")

let private randomTypes =
    Types()
        .That()
        .HaveFullName("System.Random")
        .Or()
        .HaveFullName("System.Security.Cryptography.RandomNumberGenerator")

let private networkTypes =
    Types()
        .That()
        // HttpMethod carries the method token in the endpoint registry.
        .HaveFullNameMatching(@"^System\.Net\.Http\.(?!HttpMethod$)")
        .Or()
        .HaveFullNameMatching(@"^System\.Net\.Sockets\.")
        .Or()
        .HaveFullName("System.Net.WebRequest")
        .Or()
        .HaveFullName("System.Net.WebClient")
        .Or()
        .HaveFullName("System.Net.Dns")

let private forbiddenCalls =
    MethodMembers()
        .That()
        .HaveFullNameMatching(@"\bSystem\.(DateTime|DateTimeOffset)::get_(Now|UtcNow|Today)\(")
        .Or()
        .AreDeclaredIn(Types().That().HaveFullName("System.TimeProvider"))
        .Or()
        .AreDeclaredIn(Types().That().HaveFullName("System.Diagnostics.Stopwatch"))
        .Or()
        .AreDeclaredIn(Types().That().HaveFullName("System.Environment"))
        .Or()
        .AreDeclaredIn(fileTypes)
        .Or()
        .AreDeclaredIn(randomTypes)
        .Or()
        .HaveFullNameMatching(@"\bSystem\.Guid::NewGuid\(")
        .Or()
        .HaveFullNameMatching(@"\bSystem\.IO\.Stream(Reader|Writer)::\.ctor\(System\.String")
        .Or()
        .AreDeclaredIn(networkTypes)
        .Or()
        .HaveFullNameMatching(@"\bSystem\.Globalization\.CultureInfo::get_Current(UI)?Culture\(")
        .Or()
        .AreDeclaredIn(Types().That().HaveFullName("System.TimeZoneInfo"))
        .Or()
        .HaveFullNameMatching(
            @"\bSystem\.Reflection\.(Assembly|RuntimeAssembly)::GetManifestResource"
        )
        .Or()
        .AreDeclaredIn(
            Types()
                .That()
                .HaveFullNameMatching(
                    @"^System\.Resources\.(ResourceManager|ResourceReader|ResourceSet)$"
                )
        )

[<RequireQualifiedAccess>]
type ArchitectureRule =
    | Pure
    | Postgres
    | Durable
    | Serializer
    | ApplicationProtocol
    | DomainBoundary

let nonEmptyTargets architecture name (provider: IObjectProvider<'T>) =
    if provider.GetObjects architecture |> Seq.isEmpty then
        invalidOp (name + ": no forbidden targets matched")

    provider

let rule architecture name (subjects: IObjectProvider<IType>) =
    let clauses = Types().That().Are(subjects).Should()

    let targets label (provider: IObjectProvider<IType>) =
        nonEmptyTargets architecture label provider

    let protocol () =
        Types().That().ResideInAssembly(Assembly.Load "ClaimCore.Protocol")
        |> targets "wire namespace"

    match name with
    | ArchitectureRule.Pure ->
        nonEmptyTargets architecture "filesystem types" fileTypes |> ignore
        nonEmptyTargets architecture "random types" randomTypes |> ignore
        nonEmptyTargets architecture "network types" networkTypes |> ignore
        clauses.NotCallAny(nonEmptyTargets architecture "ambient calls" forbiddenCalls) :> IArchRule
    | ArchitectureRule.Postgres ->
        clauses.NotDependOnAny(
            Types().That().ResideInAssembly(Assembly.Load "ClaimCore.Postgres")
            |> targets "Postgres"
        )
        :> IArchRule
    | ArchitectureRule.Durable
    | ArchitectureRule.ApplicationProtocol -> clauses.NotDependOnAny(protocol ()) :> IArchRule
    | ArchitectureRule.Serializer ->
        clauses.NotDependOnAny(
            Types().That().HaveFullName("System.Text.Json.JsonSerializer")
            |> targets "JsonSerializer"
        )
        :> IArchRule
    | ArchitectureRule.DomainBoundary ->
        clauses.NotDependOnAny(
            Types()
                .That()
                .ResideInAssembly(Assembly.Load "ClaimCore.Protocol")
                .Or()
                .ResideInAssembly(Assembly.Load "ClaimCore.Application")
                .Or()
                .ResideInAssembly(Assembly.Load "ClaimCore.RecordFormat")
            |> targets "Domain boundary"
        )
        :> IArchRule

// Product subjects must be assembly-scoped: F# task/async/closure bodies live under
// <StartupCode$...>, outside the declaring module's type and namespace.
let subjects (assembly: System.Reflection.Assembly) =
    Types().That().ResideInAssembly assembly

let check architecture name project selected =
    let policy = rule architecture name selected

    if not (policy.HasNoViolations architecture) then
        let details = policy.Evaluate architecture |> Seq.map string |> String.concat "\n"
        invalidOp (project + " " + string name + ": " + details)
