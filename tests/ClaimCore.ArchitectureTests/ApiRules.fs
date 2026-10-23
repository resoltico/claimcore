module internal ClaimCore.ArchitectureTests.ApiRules

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

let rule name (subjects: IObjectProvider<IType>) =
    let clauses = Types().That().Are(subjects).Should()

    match name with
    | "pure" -> clauses.NotCallAny(forbiddenCalls) :> IArchRule
    | "postgres" ->
        clauses.NotDependOnAny(Types().That().ResideInAssembly(Assembly.Load "ClaimCore.Postgres"))
        :> IArchRule
    | "serializer" ->
        clauses.NotDependOnAny(Types().That().HaveFullName("System.Text.Json.JsonSerializer"))
        :> IArchRule
    | _ -> invalidArg "name" ("Unknown architecture rule " + name)

let check architecture name project (subjects: IObjectProvider<IType>) =
    if subjects.GetObjects architecture |> Seq.isEmpty then
        invalidOp (project + " " + name + ": no compiled subjects matched")

    let policy = rule name subjects

    if not (policy.HasNoViolations architecture) then
        let details = policy.Evaluate architecture |> Seq.map string |> String.concat "\n"
        invalidOp (project + " " + name + ": " + details)
