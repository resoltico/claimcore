module ClaimCore.Tests.BuildIdentityTests

open System
open System.IO
open System.Reflection
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml.Linq
open Expecto
open ClaimCore.Application
open ClaimCore.Cli

let private sourceProperty name =
    let file =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../Directory.Build.props"))

    XDocument.Load(file).Descendants(XName.Get(name))
    |> Seq.exactlyOne
    |> fun item -> item.Value

let private sourceIdentityTests =
    testList
        "compiled source identity"
        [
            testCase
                "native core identity comes from the SDK attributes, matching the sole source"
                (fun () ->
                    let identity = BuildIdentity.current
                    let expected = sourceProperty "Version"
                    let numeric = Regex.Match(expected, @"^[0-9]+\.[0-9]+\.[0-9]+").Value + ".0"
                    Expect.equal identity.Version expected "No independent source constant"
                    Expect.equal identity.Product (sourceProperty "Product") "Product identity"
                    Expect.equal identity.AssemblyVersion numeric "Derived CLR version"
                    Expect.equal identity.FileVersion numeric "Derived file version"

                    Expect.equal
                        SemanticContract.current.Application
                        identity.Product
                        "Shared semantic product name")
            testCase "CLI v3 discovery renders compiled release identity" (fun () ->
                let bytes = Discovery.versionJson ()

                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))

                Expect.equal
                    (document.RootElement.GetProperty("version").GetString())
                    BuildIdentity.current.Version
                    "Compiled version"

                Expect.equal
                    (document.RootElement.GetProperty("application").GetString())
                    BuildIdentity.current.Product
                    "Compiled product")
        ]

let private consumerIdentityTests =
    testList
        "identity consumers and compatibility"
        [
            testCase
                "a non-product assembly cannot be accepted as a compatible first-party component"
                (fun () ->
                    Expect.throws
                        (fun () -> BuildIdentity.requireCompatibleAssembly typeof<string>.Assembly)
                        "No missing-attribute fallback")
            testCase
                "current test and application assemblies have matching release identity"
                (fun () ->
                    BuildIdentity.requireCompatibleAssembly (Assembly.GetExecutingAssembly()))
        ]

let private repositoryRoot () =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

let private productProjectFiles repository =
    let extensions = set [ ".fsproj"; ".props"; ".targets" ]
    let generated = set [ "bin"; "obj" ]

    let relevant (path: string) =
        (Path.GetExtension(path) |> Option.ofObj |> Option.exists extensions.Contains)
        && (Path.GetRelativePath(repository, path).Split(Path.DirectorySeparatorChar)
            |> Array.exists generated.Contains
            |> not)

    let rootFiles =
        Directory.EnumerateFiles(repository, "*", SearchOption.TopDirectoryOnly)

    let projectFiles =
        [ "src"; "tests" ]
        |> Seq.collect (fun folder ->
            Directory.EnumerateFiles(
                Path.Combine(repository, folder),
                "*",
                SearchOption.AllDirectories
            ))

    Seq.append rootFiles projectFiles |> Seq.filter relevant |> Seq.toList

let private versionDeclarations repository =
    let names =
        set
            [
                "Version"
                "VersionPrefix"
                "VersionSuffix"
                "AssemblyVersion"
                "FileVersion"
                "InformationalVersion"
            ]

    productProjectFiles repository
    |> List.collect (fun path ->
        XDocument.Load(path).Descendants()
        |> Seq.filter (fun element -> names.Contains(element.Name.LocalName))
        |> Seq.map (fun element -> Path.GetRelativePath(repository, path), element.Name.LocalName)
        |> Seq.toList)

let private frontendAuthorsVersion repository =
    use manifest =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "web/package.json")))

    let mutable independentVersion = Unchecked.defaultof<JsonElement>
    manifest.RootElement.TryGetProperty("version", &independentVersion)

let private versionOwnershipTests =
    testList
        "version ownership"
        [
            testCase "only Directory.Build.props authors product version properties" (fun () ->
                let repository = repositoryRoot ()
                let declarations = versionDeclarations repository
                Expect.isNonEmpty declarations "Version properties must exist"

                declarations
                |> List.iter (fun (path, _) ->
                    Expect.equal path "Directory.Build.props" "No second MSBuild version source")

                Expect.isFalse
                    (frontendAuthorsVersion repository)
                    "The frontend has no independent product version")
        ]

let tests =
    testList
        "single-owner compiled release identity"
        [ sourceIdentityTests; consumerIdentityTests; versionOwnershipTests ]
