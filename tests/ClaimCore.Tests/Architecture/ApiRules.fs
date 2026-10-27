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
    | DurableNames
    | Serializer
    | ServiceModelWithoutProtocol
    | ServiceSurface
    | DomainJsonFree
    | VocabularyModuleNaming
    | DomainBoundary

let nonEmptyTargets architecture name (provider: IObjectProvider<'T>) =
    if provider.GetObjects architecture |> Seq.isEmpty then
        invalidOp (name + ": no forbidden targets matched")

    provider

/// Reflection identifies declarations; ArchUnit evaluates the compiled product and negative subjects.
let vocabularyModules () =
    [
        "ClaimCore.Domain"
        "ClaimCore.Protocol"
        "ClaimCore.RecordFormat"
        "ClaimCore.Application"
        "ClaimCore.Postgres"
        "claimcore"
        "claimcore-server"
        "claimcore-database"
        "ClaimCore.Tests"
    ]
    |> List.collect (fun name -> Assembly.Load(name).GetTypes() |> Array.toList)
    |> List.choose (fun declaration ->
        let property =
            declaration.GetProperty(
                "vocabulary",
                BindingFlags.Public
                ||| BindingFlags.NonPublic
                ||| BindingFlags.Static
                ||| BindingFlags.DeclaredOnly
            )

        match Option.ofObj property with
        | Some property when
            property.PropertyType.IsGenericType
            && property.PropertyType.GetGenericTypeDefinition() =
                typedefof<ClaimCore.Domain.Vocabulary<_>>
            ->
            Some(declaration, property.PropertyType.GetGenericArguments()[0])
        | _ -> None)


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
    | ArchitectureRule.DurableNames ->
        let names =
            vocabularyModules ()
            |> List.filter (fun (declaration, _) ->
                declaration.Assembly = typeof<ClaimCore.Domain.CaseActKind>.Assembly)
            |> List.map (fun (declaration, _) ->
                System.Text.RegularExpressions.Regex.Escape(nonNull declaration.FullName))
            |> String.concat "|"

        let pattern = "^(" + names + ")$"

        Types().That().HaveFullNameMatching(pattern)
        |> targets "Domain vocabulary modules"
        |> ignore

        protocol () |> ignore

        clauses.NotDependOnAny(
            Types()
                .That()
                .ResideInAssembly(Assembly.Load "ClaimCore.Protocol")
                .Or()
                .HaveFullNameMatching(pattern)
            |> targets "durable spelling boundary"
        )
        :> IArchRule
    | ArchitectureRule.ServiceModelWithoutProtocol ->
        clauses.NotDependOnAny(protocol ()) :> IArchRule
    | ArchitectureRule.Serializer ->
        clauses.NotDependOnAny(
            Types().That().HaveFullName("System.Text.Json.JsonSerializer")
            |> targets "JsonSerializer"
        )
        :> IArchRule
    | ArchitectureRule.ServiceSurface ->
        let allowed =
            [
                "Service"
                "ServiceContext"
                "IStore"
                "IReadTx"
                "IWriteTx"
                "IBusinessCalendar"
                "RuntimeOpenFault"
                "StoreFailure"
                "Verdict"
                "LedgerHead"
                "StoredCase"
                "EntryTarget"
                "StoredEntry"
                "CaseProjection"
                "EntryDraft"
                "AuthorityProjection"
                "AuthorityQuery"
                "CasePageQuery"
                "HistoryPageQuery"
                "ActorPageQuery"
                "GrantPageQuery"
                "FenceKey"
                "Page"
                "ReadOperation"
                "WriteOperation"
            ]
            |> List.map System.Text.RegularExpressions.Regex.Escape
            |> String.concat "|"

        let permitted =
            "^ClaimCore\\.Application\\.(" + allowed + ")(?:`[0-9]+)?(?:\\+.*)?$"

        clauses.NotDependOnAny(
            Types()
                .That()
                .ResideInAssembly(Assembly.Load "ClaimCore.Application")
                .And()
                .DoNotHaveFullNameMatching(permitted)
            |> targets "Application implementation outside the service and ports"
        )
        :> IArchRule
    | ArchitectureRule.DomainJsonFree ->
        clauses.NotDependOnAny(
            Types().That().ResideInAssembly(Assembly.Load "ClaimCore.Json")
            |> targets "JSON assembly"
        )
        :> IArchRule
    | ArchitectureRule.VocabularyModuleNaming ->
        let declarations = vocabularyModules ()

        let names =
            declarations
            |> List.map (fun (declaration, _) ->
                System.Text.RegularExpressions.Regex.Escape(nonNull declaration.FullName))
            |> String.concat "|"

        Types().That().HaveFullNameMatching("^(" + names + ")$")
        |> targets "vocabulary modules"
        |> ignore

        clauses.FollowCustomCondition(
            System.Func<IType, bool>(fun candidate ->
                declarations
                |> List.tryFind (fun (declaration, _) ->
                    nonNull declaration.FullName = candidate.FullName)
                |> Option.forall (fun (declaration, value) ->
                    declaration.Assembly = value.Assembly
                    && nonNull declaration.FullName =
                        nonNull value.Namespace + "." + value.Name + "Module")),
            "declare vocabulary in the value type's module",
            "Vocabulary must be declared by <Type>Module in the value type's namespace and assembly"
        )
        :> IArchRule
    | ArchitectureRule.DomainBoundary ->
        for assembly in
            [
                "ClaimCore.Protocol"
                "ClaimCore.Application"
                "ClaimCore.RecordFormat"
                "ClaimCore.Json"
                "ClaimCore.Postgres"
            ] do
            Types().That().ResideInAssembly(Assembly.Load assembly)
            |> targets ("Domain excludes " + assembly)
            |> ignore

        clauses.NotDependOnAny(
            Types()
                .That()
                .ResideInAssembly(Assembly.Load "ClaimCore.Protocol")
                .Or()
                .ResideInAssembly(Assembly.Load "ClaimCore.Application")
                .Or()
                .ResideInAssembly(Assembly.Load "ClaimCore.RecordFormat")
                .Or()
                .ResideInAssembly(Assembly.Load "ClaimCore.Json")
                .Or()
                .ResideInAssembly(Assembly.Load "ClaimCore.Postgres")
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
