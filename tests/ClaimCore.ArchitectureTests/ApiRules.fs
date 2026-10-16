module internal ClaimCore.ArchitectureTests.ApiRules

open System.Reflection
open ArchUnitNET.Domain
open ArchUnitNET.Fluent
open type ArchUnitNET.Fluent.ArchRuleDefinition

let names =
    set
        [
            "clientAvoidsServerSemantics"
            "httpHostAvoidsDirectSemantics"
            "onlyTheseMayReferencePostgres"
            "onlyTheseMayReferenceHosting"
            "freeOfClockFileEnvironmentRandomnessAndNetwork"
            "onlyTheseMayUseTimeZoneInfo"
            "recordFormatAvoidsFrameworkSerialization"
        ]

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
        .HaveFullNameMatching(@"^System\.Net\.Http\.")
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
        .AreDeclaredIn(networkTypes)
        .Or()
        .HaveFullNameMatching(@"\bSystem\.Globalization\.CultureInfo::get_Current(UI)?Culture\(")

let private semanticTypes includeHosting =
    let forbidden =
        Types()
            .That()
            .ResideInAssembly(Assembly.Load "ClaimCore.Domain")
            .Or()
            .ResideInAssembly(Assembly.Load "ClaimCore.RecordFormat")
            .Or()
            .ResideInAssembly(Assembly.Load "ClaimCore.Application")
            .Or()
            .ResideInAssembly(Assembly.Load "ClaimCore.Contracts")
            .Or()
            .ResideInAssembly(Assembly.Load "ClaimCore.Postgres")

    if includeHosting then
        forbidden.Or().ResideInAssembly(Assembly.Load "ClaimCore.Hosting")
    else
        forbidden

let rule name (subjects: IObjectProvider<IType>) =
    let clauses = Types().That().Are(subjects).Should()

    match name with
    | "clientAvoidsServerSemantics"
    | "httpHostAvoidsDirectSemantics" ->
        clauses.NotDependOnAny(semanticTypes (name = "clientAvoidsServerSemantics")) :> IArchRule
    | "onlyTheseMayReferencePostgres" ->
        clauses.NotDependOnAny(Types().That().ResideInAssembly(Assembly.Load "ClaimCore.Postgres"))
        :> IArchRule
    | "onlyTheseMayReferenceHosting" ->
        clauses.NotDependOnAny(Types().That().ResideInAssembly(Assembly.Load "ClaimCore.Hosting"))
        :> IArchRule
    | "onlyTheseMayUseTimeZoneInfo" ->
        clauses.NotDependOnAny(Types().That().HaveFullName("System.TimeZoneInfo")) :> IArchRule
    | "recordFormatAvoidsFrameworkSerialization" ->
        clauses.NotDependOnAny(
            Types()
                .That()
                .HaveFullName("System.Text.Json.Utf8JsonWriter")
                .Or()
                .HaveFullName("System.Text.Json.JsonSerializer")
        )
        :> IArchRule
    | "freeOfClockFileEnvironmentRandomnessAndNetwork" ->
        clauses.NotCallAny(forbiddenCalls) :> IArchRule
    | _ -> invalidArg "name" ("Unknown architecture rule " + name)

let check architecture name project (subjects: IObjectProvider<IType>) =
    if subjects.GetObjects architecture |> Seq.isEmpty then
        invalidOp (project + " " + name + ": no compiled subjects matched")

    let policy = rule name subjects

    if not (policy.HasNoViolations architecture) then
        let details = policy.Evaluate architecture |> Seq.map string |> String.concat "\n"
        invalidOp (project + " " + name + ": " + details)
