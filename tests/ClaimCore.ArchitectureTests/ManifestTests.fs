module ClaimCore.ArchitectureTests.ManifestTests

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Xml.Linq
open Expecto
open ClaimCore.TestSupport

let private ordinal (left: string) right =
    StringComparer.Ordinal.Compare(left, right)

let private repositoryProjects () =
    RepositoryRoot.find () |> ProjectReferences.loadRepositoryProjects

/// Nothing in the repository builds outside the reviewed classification.
let private everyProjectIsClassified () =
    let root = RepositoryRoot.find ()

    let discovered =
        repositoryProjects ()
        |> List.map (fun path ->
            Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
        |> Set.ofList

    let classified = ProductPolicy.components |> List.map _.Project |> Set.ofList

    let missing = Set.difference discovered classified
    let phantom = Set.difference classified discovered

    Expect.isEmpty missing "Every repository project must be classified in architecture.json"
    Expect.isEmpty phantom "architecture.json must not classify a project that does not exist"

/// Declared edges, for every tier, equal the reviewed permission set exactly.
let private declaredGraphMatchesManifest () =
    let projects = repositoryProjects ()
    let names = ProjectReferences.names projects

    let failures =
        projects
        |> List.collect (fun path ->
            ProjectReferences.violations names ProductPolicy.allowed path (XDocument.Load path))

    if not failures.IsEmpty then
        failtest (failures |> List.sortWith ordinal |> String.concat Environment.NewLine)

/// A shipped component never links repository tooling or verification code.
let private productNeverDependsOnNonProduct () =
    let nonProduct =
        ProductPolicy.components
        |> List.filter (fun item -> item.Tier <> "product")
        |> List.map _.Name
        |> Set.ofList

    let failures =
        ProductPolicy.product
        |> List.collect (fun item ->
            item.DependsOn
            |> List.filter nonProduct.Contains
            |> List.map (fun target -> item.Name + " depends on non-product " + target))

    Expect.isEmpty failures "Product components depend only on product components"

/// The reviewed direct graph must stay acyclic, so a component's dependencies can be reasoned
/// about without a fixed point.
let private graphIsAcyclic () =
    let edges = ProductPolicy.allowed

    let rec walk visiting name =
        if Set.contains name visiting then
            Some(name + " participates in a component dependency cycle")
        else
            let next = Set.add name visiting

            edges |> Map.tryFind name |> Option.defaultValue [] |> List.tryPick (walk next)

    let failures =
        ProductPolicy.components |> List.choose (fun item -> walk Set.empty item.Name)

    Expect.isEmpty failures "The reviewed component graph is acyclic"

let private internalsGrants (assembly: Assembly) =
    assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
    // A grant may carry a public-key clause; the assembly identity is its first field.
    |> Seq.map (fun attribute -> attribute.AssemblyName.Split(',').[0].Trim())
    |> Set.ofSeq

/// `internal` is this architecture's primary encapsulation mechanism, so every grant that widens
/// it is a reviewed edge rather than an unnoticed attribute.
let private internalsVisibilityIsReviewed () =
    let failures =
        ProductPolicy.product
        |> List.collect (fun item ->
            let assembly =
                ProductModel.assemblies.Value
                |> Array.find (fun candidate -> candidate.GetName().Name = item.Name)

            let actual = internalsGrants assembly
            let reviewed = Set.ofList item.InternalsVisibleTo

            [
                for surplus in
                    Set.difference actual reviewed |> Set.toList |> List.sortWith ordinal do
                    item.Name + " grants unreviewed internals access to " + surplus

                for stale in Set.difference reviewed actual |> Set.toList |> List.sortWith ordinal do
                    item.Name + " is reviewed for an absent internals grant to " + stale
            ])

    if not failures.IsEmpty then
        failtest (String.concat Environment.NewLine failures)

/// A grant to an unclassified assembly cannot be reviewed at all.
let private internalsGrantsNameClassifiedComponents () =
    let classified = ProductPolicy.components |> List.map _.Name |> Set.ofList

    let failures =
        ProductPolicy.components
        |> List.collect (fun item ->
            item.InternalsVisibleTo
            |> List.filter (fun target -> not (classified.Contains target))
            |> List.map (fun target -> item.Name + " grants internals to unclassified " + target))

    Expect.isEmpty failures "Every internals grant names a classified component"

/// A grant is only meaningful to a component that actually links the granting one.
let private internalsGrantsFollowDeclaredEdges () =
    let failures =
        ProductPolicy.components
        |> List.collect (fun item ->
            item.InternalsVisibleTo
            |> List.filter (fun target ->
                not (List.contains item.Name (ProductPolicy.find target).DependsOn))
            |> List.map (fun target ->
                item.Name + " grants internals to " + target + ", which does not reference it"))

    Expect.isEmpty failures "Internals grants follow declared project edges"

let private negativeControls () =
    let names = Map.ofList [ "/repo/ClaimCore.Web.fsproj", "ClaimCore.Web" ]
    let permissions = Map.ofList [ "ClaimCore.Cli", [ "ClaimCore.Web" ] ]

    let document = XDocument.Parse "<Project><ItemGroup></ItemGroup></Project>"

    let stale =
        ProjectReferences.violations names permissions "/repo/ClaimCore.Cli.fsproj" document

    Expect.isNonEmpty stale "A permitted but undeclared edge must fail as a stale permission"

    Expect.stringContains
        stale.Head
        "undeclared project reference"
        "The stale permission names its cause"

    let granted =
        ProductPolicy.components
        |> List.collect (fun item -> item.InternalsVisibleTo)
        |> Set.ofList

    Expect.isNonEmpty granted "The manifest reviews at least one internals grant"

let tests =
    testList
        "architecture manifest"
        [
            testCase "every repository project is classified" everyProjectIsClassified
            testCase
                "declared project edges match the manifest exactly"
                declaredGraphMatchesManifest
            testCase
                "product components depend only on product components"
                productNeverDependsOnNonProduct
            testCase "the reviewed component graph is acyclic" graphIsAcyclic
            testCase
                "compiled internals grants match the manifest exactly"
                internalsVisibilityIsReviewed
            testCase
                "internals grants name classified components"
                internalsGrantsNameClassifiedComponents
            testCase
                "internals grants follow declared project edges"
                internalsGrantsFollowDeclaredEdges
            testCase "stale permissions and empty grant sets are detected" negativeControls
        ]
