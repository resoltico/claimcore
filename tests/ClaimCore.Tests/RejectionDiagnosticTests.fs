module ClaimCore.Tests.RejectionDiagnosticTests

open System
open System.Globalization
open System.Text.Json
open Microsoft.FSharp.Reflection
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain
open ClaimCore.Qualification

let private token =
    RejectionDiagnostics.describe
    >> RejectionDiagnostics.identifier
    >> RejectionDiagnosticIds.token

let private parsed (bytes: byte array) =
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    document.RootElement.Clone()

let private cli rejection =
    CliWireCodec.caseGet "case.get" (QueryOutcome.Rejected rejection)

let private web rejection =
    WebWireCodec.get (QueryOutcome.Rejected rejection)

let private catalogueCoverage () =
    let declared = RejectionDiagnosticIds.all |> List.map snd
    let examples = RejectionExamples.all |> List.map fst

    Expect.equal
        (List.sort examples)
        (List.sort declared)
        "Every declared identity has a reviewed exemplar"

    Expect.equal (Set.ofList declared).Count declared.Length "No token collision"

    Expect.equal
        declared.Length
        (FSharpType.GetUnionCases typeof<RejectionDiagnosticId>).Length
        "No unregistered union case"

    for expected, rejection in RejectionExamples.all do
        Expect.equal (token rejection) expected "Stable explicit token"
        Expect.isNonEmpty (RejectionPresentation.render rejection) "Total outward presentation"
        ignore rejection.Code
        ignore rejection.Action

let private safeParameters () =
    for id, rejection in RejectionExamples.all do
        let actual = RejectionDiagnostics.describe rejection |> RejectionDiagnostics.values

        let declared =
            SemanticContract.current.RejectionDiagnostics
            |> List.find (fun item -> item.Id = id)

        Expect.equal (actual |> List.map fst) (declared.Parameters |> List.map _.Name) id

        for name, value in actual do
            let parameter = declared.Parameters |> List.find (fun item -> item.Name = name)

            Expect.isTrue
                (value >= parameter.Minimum && value <= parameter.Maximum)
                "Owned numeric constraint"

let private closedPayloads () =
    let rec inspect (value: Type) =
        if value <> typeof<int> && value <> typeof<int64> then
            Expect.isTrue
                (FSharpType.IsUnion value)
                ("No open or string payload: " + value.FullName)

            for case in FSharpType.GetUnionCases value do
                for field in case.GetFields() do
                    inspect field.PropertyType

    inspect typeof<Rejection>
    inspect typeof<DiagnosticParameters>

    Expect.isFalse
        (FSharpType.IsRecord typeof<Rejection>)
        "Reasons cannot be mixed in a mutable result record"

    Expect.isNull (typeof<Rejection>.GetProperty("Message")) "No core-owned presentation accessor"

let private targetCoverage () =
    let targets = InputTargets.all |> List.map snd
    Expect.equal (Set.ofList targets).Count targets.Length "Unique safe field tokens"

    Expect.equal
        targets.Length
        (FSharpType.GetUnionCases typeof<InputTarget>).Length
        "No missing target mapping"

    Expect.equal
        (InputTargets.token InputTarget.ExpectedVersion)
        "expectedRevision"
        "Public request vocabulary"

    Expect.equal (InputTargets.token InputTarget.Version) "revision" "Public response vocabulary"

let private wireParity () =
    for id, rejection in RejectionExamples.all do
        let result = cli rejection
        let cliValue = (parsed result.Bytes).GetProperty("outcome").GetProperty("rejection")
        let webValue = (parsed (web rejection)).GetProperty("outcome").GetProperty("data")
        Expect.equal result.ExitCode 2 "A rejection remains a definite refusal"
        Expect.equal (cliValue.GetRawText()) (webValue.GetRawText()) id

        Expect.equal
            (cliValue.GetProperty("diagnostic").GetProperty("id").GetString())
            id
            "Machine identity is present"

let private specificReasons () =
    let first = Rejection.Domain DomainError.CorrectionNoChanges
    let second = Rejection.Domain DomainError.CorrectionRequiresExistingValue
    Expect.equal first.Code second.Code "Same coarse input class"
    Expect.notEqual (token first) (token second) "Different actionable causes"
    Expect.equal first.Action RecommendedAction.NoneRequired "No changed business guidance"

    Expect.equal
        (Rejection.Domain(DomainError.VersionConflict 12L)).ActualVersion
        (Some 12L)
        "Concurrency metadata retained"

    Expect.equal
        Rejection.IdempotencyConflict.Action
        RecommendedAction.StopAndInvestigate
        "Never automatic rebase"

    Expect.equal
        Rejection.OperationRevoked.Action
        RecommendedAction.ReadCurrent
        "Revocation guidance retained"

let private taxonomyIdentity () =
    let current = SemanticContract.current
    let original = SemanticContract.fingerprint current
    let first = current.RejectionDiagnostics.Head

    let changed =
        [
            { first with
                Id = first.Id + "_CHANGED"
            }
            { first with
                Parameters =
                    [
                        {
                            Name = "maximumCharacters"
                            Minimum = 1
                            Maximum = 200
                        }
                    ]
            }
        ]

    for diagnostic in changed do
        let contract =
            { current with
                RejectionDiagnostics = diagnostic :: current.RejectionDiagnostics.Tail
            }

        Expect.notEqual
            (SemanticContract.fingerprint contract)
            original
            "Machine taxonomy is semantic identity"

        let projection = ContractProjection.create contract
        let baseline = ContractProjection.create current

        Expect.notEqual
            (ContractRenderers.cliFingerprint projection)
            (ContractRenderers.cliFingerprint baseline)
            "CLI metadata changes"

        Expect.notEqual
            (ContractRenderers.webFingerprint projection)
            (ContractRenderers.webFingerprint baseline)
            "Web metadata changes"

let private parameterIdentity () =
    let current = SemanticContract.current
    let original = SemanticContract.fingerprint current

    let descriptor =
        current.RejectionDiagnostics
        |> List.find (fun item -> not item.Parameters.IsEmpty)

    let first = descriptor.Parameters.Head

    for parameter in
        [
            { first with
                Name = first.Name + "Changed"
            }
            { first with
                Minimum = first.Minimum + 1
            }
            { first with
                Maximum = first.Maximum - 1
            }
        ] do
        let changed =
            { descriptor with
                Parameters = parameter :: descriptor.Parameters.Tail
            }

        let contract =
            { current with
                RejectionDiagnostics =
                    current.RejectionDiagnostics
                    |> List.map (fun item -> if item.Id = changed.Id then changed else item)
            }

        Expect.notEqual
            (SemanticContract.fingerprint contract)
            original
            "Name and both bounds are covered"

let private cultures () =
    let beforeCulture = CultureInfo.CurrentCulture
    let beforeUi = CultureInfo.CurrentUICulture
    let expected = RejectionExamples.all |> List.map (snd >> cli >> _.Bytes)

    try
        for culture in [ "en-US"; "lv-LV"; "tr-TR"; "ar-SA" ] do
            CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo culture
            CultureInfo.CurrentUICulture <- CultureInfo.GetCultureInfo culture

            Expect.equal
                (RejectionExamples.all |> List.map (snd >> cli >> _.Bytes))
                expected
                "Machine data and default presentation do not use ambient locale"
    finally
        CultureInfo.CurrentCulture <- beforeCulture
        CultureInfo.CurrentUICulture <- beforeUi

let tests =
    testList
        "typed rejection diagnostics"
        [
            testCase
                "every closed identity has one explicit token and a complete projection"
                catalogueCoverage
            testCase "diagnostic arguments match the closed safe parameter schema" safeParameters
            testCase
                "native rejection payloads cannot contain free text or arbitrary bags"
                closedPayloads
            testCase "field targets are closed and use public revision vocabulary" targetCoverage
            testCase
                "CLI and Web project identical diagnostics without changing rejection exit semantics"
                wireParity
            testCase
                "specific causes remain distinct under coarse codes and preserve actions"
                specificReasons
            testCase
                "diagnostic identities and parameter shapes participate in all fingerprints"
                taxonomyIdentity
            testCase
                "diagnostic parameter names and both bounds participate in semantic identity"
                parameterIdentity
            testCase "ambient culture never selects rejection identity or parameter values" cultures
        ]
