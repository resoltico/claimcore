module ClaimCore.ArchitectureTests.OwnershipTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Expecto
open ArchUnitNET.Fluent

let private architecture = ProductModel.architecture
let private select = ProductModel.select

/// Components with no permitted effect boundary: they compute over values supplied by a caller.
let private pureParts = [ "Domain"; "RecordFormat"; "Application"; "Contracts" ]

let private forbiddenTypes =
    [
        typeof<Console>
        typeof<File>
        typeof<Directory>
        typeof<Environment>
        typeof<Random>
        typeof<Thread>
    ]

/// Ambient identity and calendar reads that would bypass the caller-supplied operation ID or the
/// installation's stored business zone.
let private forbiddenCalls =
    [
        typeof<DateTime>, "get_Now"
        typeof<DateTime>, "get_UtcNow"
        typeof<DateTime>, "get_Today"
        typeof<DateTimeOffset>, "get_Now"
        typeof<DateTimeOffset>, "get_UtcNow"
        typeof<TimeZoneInfo>, "get_Local"
        typeof<Guid>, "NewGuid"
    ]

let private deterministic subject =
    testCase (subject + " avoids selected ambient clock, identity and host IO APIs") (fun () ->
        let model = architecture.Value
        let subjects = select subject

        for target in forbiddenTypes do
            let forbidden = ArchRuleDefinition.Types().That().Are(target)
            Inspection.requireSelection model forbidden |> ignore
            Inspection.check model (subjects.Should().NotDependOnAny(forbidden))

        for target, methodName in forbiddenCalls do
            let forbidden =
                ArchRuleDefinition
                    .MethodMembers()
                    .That()
                    .AreDeclaredIn(target)
                    .And()
                    .HaveNameContaining(methodName)

            Inspection.requireSelection model forbidden |> ignore
            Inspection.check model (subjects.Should().NotCallAny(forbidden)))

/// The Web host is one assembly, so its composition root is confined by a compiled rule rather
/// than by a project edge. The selector is anchored to the `Program` module the author declared,
/// and the positive assertion keeps the rule from passing merely because composition moved out of
/// the selector's reach.
/// Contracts owns every CLI-v3 and Web-v2 codec, including the database-free discovery payloads.
/// A renderer that authored wire JSON itself would fork the generated contract without its schema,
/// corpus, or fingerprint noticing, so no wire-contract renderer may reach a JSON writer at all.
/// `Database` is deliberately outside this set: it publishes no wire contract and references no
/// contract projection, so its administrative output is not a protocol surface.
let private wireRenderersDoNotAuthorJson () =
    let model = architecture.Value
    let writer = ArchRuleDefinition.Types().That().Are(typeof<Utf8JsonWriter>)
    let owner = select "Contracts"

    Inspection.requireSelection model writer |> ignore
    Inspection.requireSelection model owner |> ignore

    Expect.isNonEmpty
        (Inspection.violations model (owner.Should().NotDependOnAny(writer)))
        "Contracts must be the component that authors wire JSON"

    for subject in [ "Cli"; "CliProtocol"; "Web" ] do
        let renderer = select subject
        Inspection.requireSelection model renderer |> ignore
        Inspection.check model (renderer.Should().NotDependOnAny(writer))

/// The installation calendar is a stored IANA zone plus one captured instant. Only the components
/// that own it may touch the host time-zone database at all: Postgres validates and stores the
/// zone, Hosting resolves it once and derives the business date. Any other component resolving or
/// converting a zone would be deriving a second calendar from the host rather than from the
/// installation, which is the hazard the stored zone exists to remove.
let private installationOwnsTheCalendar () =
    let model = architecture.Value
    let zones = ArchRuleDefinition.Types().That().Are(typeof<TimeZoneInfo>)

    Inspection.requireSelection model zones |> ignore

    for owner in [ "Hosting"; "Postgres" ] do
        let component' = select owner
        Inspection.requireSelection model component' |> ignore

        Expect.isNonEmpty
            (Inspection.violations model (component'.Should().NotDependOnAny(zones)))
            ("The installation calendar owner must resolve zones: " + owner)

    for subject in
        [
            "Domain"
            "RecordFormat"
            "Application"
            "Contracts"
            "Cli"
            "CliProtocol"
            "Web"
            "Database"
        ] do
        let other = select subject
        Inspection.requireSelection model other |> ignore
        Inspection.check model (other.Should().NotDependOnAny(zones))

let private persistenceDoesNotDecide () =
    let model = architecture.Value

    let storage = select "Postgres"

    let decision =
        ArchRuleDefinition.MethodMembers().That().HaveFullNameContaining("ClaimModule::decide(")

    Inspection.requireSelection model storage |> ignore
    Inspection.requireSelection model decision |> ignore
    Inspection.check model (storage.Should().NotCallAny(decision))

let private adaptersDoNotDecide () =
    let model = architecture.Value

    let decision =
        ArchRuleDefinition.MethodMembers().That().HaveFullNameContaining("ClaimModule::decide(")

    Inspection.requireSelection model decision |> ignore

    for subject in [ "Cli"; "CliProtocol"; "Web"; "Database"; "Hosting" ] do
        let adapter = select subject
        Inspection.requireSelection model adapter |> ignore
        Inspection.check model (adapter.Should().NotCallAny(decision))

/// The composition root is the only component that binds a store to the core, so it is also the
/// only one that may name Application's internal ports.
let private compositionOwnsCoreConstruction () =
    let model = architecture.Value

    let construction =
        ArchRuleDefinition
            .MethodMembers()
            .That()
            .HaveFullNameContaining("CoreApi")
            .And()
            .HaveNameContaining("create")

    Inspection.requireSelection model construction |> ignore

    let composition = select "Hosting"
    Inspection.requireSelection model composition |> ignore

    Expect.isNonEmpty
        (Inspection.violations model (composition.Should().NotCallAny(construction)))
        "The composition root must be the component that constructs the typed core"

    for subject in [ "Cli"; "CliProtocol"; "Web"; "Database"; "Contracts"; "Postgres" ] do
        let other = select subject
        Inspection.requireSelection model other |> ignore
        Inspection.check model (other.Should().NotCallAny(construction))

/// The Web and CLI hosts must not reach the schema-owner administration surface, which changes
/// durable structure outside any case-work transaction.
let private caseWorkHostsCannotAdminister () =
    let model = architecture.Value

    let administration =
        ArchRuleDefinition
            .Types()
            .That()
            .HaveFullNameMatching(
                @"^ClaimCore\.Postgres\.(?:SchemaBaseline|PreparationPruning|InstallationBusinessZone)(?:[+/.].*)?$"
            )

    Inspection.requireSelection model administration |> ignore

    for subject in [ "Cli"; "CliProtocol"; "Web" ] do
        let host = select subject
        Inspection.requireSelection model host |> ignore
        Inspection.check model (host.Should().NotDependOnAny(administration))

let tests =
    testList
        "component effect ownership"
        ([
            testCase "wire-contract renderers cannot author JSON" wireRenderersDoNotAuthorJson
            testCase
                "only the installation calendar owners resolve time zones"
                installationOwnsTheCalendar
            testCase "persistence cannot call the domain decision directly" persistenceDoesNotDecide
            testCase "adapters cannot call the domain decision directly" adaptersDoNotDecide
            testCase
                "only the composition root constructs the typed core"
                compositionOwnsCoreConstruction
            testCase
                "case-work hosts cannot reach schema administration"
                caseWorkHostsCannotAdminister
         ]
         @ (pureParts |> List.map deterministic))
