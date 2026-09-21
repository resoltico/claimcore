module ClaimCore.Tests.OutcomeDiagnosticTests

open System
open System.Globalization
open System.Text.Json
open Microsoft.FSharp.Reflection
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts

let private parsed (bytes: byte array) =
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    document.RootElement.Clone()

let private outcome name bytes =
    (parsed bytes).GetProperty("outcome").GetProperty(name: string)

let private faultVocabulary () =
    for reason, id, code, action in FaultDiagnosticExamples.all do
        Expect.equal (CoreFaults.token reason) id "Stable fault identity"
        Expect.equal reason.Code code "Reviewed coarse fault code"
        Expect.equal reason.Action action "Reviewed fault guidance"
        Expect.isNonEmpty (CoreFaultPresentation.render reason) "Total outward presentation"

    Expect.equal
        (CoreFaults.all |> List.map fst |> Set.ofList)
        (FaultDiagnosticExamples.all
         |> List.map (fun (reason, _, _, _) -> reason)
         |> Set.ofList)
        "Every compiled cause has a reviewed example"

let private recoveryVocabulary () =
    for reason, id, code, action in RecoveryDiagnosticExamples.all do
        Expect.equal (RecoveryRejections.token reason) id "Stable lifecycle identity"
        Expect.equal reason.Code code "Reviewed coarse refusal code"
        Expect.equal reason.Action action "Reviewed refusal guidance"
        Expect.isNonEmpty (RecoveryRejectionPresentation.render reason) "Total outward presentation"

    Expect.equal
        (RecoveryRejections.all |> List.map fst |> Set.ofList)
        (RecoveryDiagnosticExamples.all
         |> List.map (fun (reason, _, _, _) -> reason)
         |> Set.ofList)
        "Every compiled refusal has a reviewed example"

let private closedReasons () =
    for value, count in
        [
            typeof<CoreFault>, CoreFaults.all.Length
            typeof<RecoveryRejection>, RecoveryRejections.all.Length
        ] do
        let cases = FSharpType.GetUnionCases value
        Expect.equal cases.Length count "No unregistered nullary cause"

        Expect.isTrue
            (cases |> Array.forall (fun item -> item.GetFields().Length = 0))
            "No arbitrary text or parameter bags"

        Expect.isNull (value.GetProperty("Message")) "Core owns no English explanation"

        for name in [ "Code"; "Action" ] do
            Expect.isFalse
                (value.GetProperty(name)
                 |> Option.ofObj
                 |> Option.map _.CanWrite
                 |> Option.defaultWith (fun () -> failtest "Required derived property is missing."))
                "Guidance cannot be independently changed"

let private catalogueSeparation () =
    let contract = SemanticContract.current

    let definitions =
        contract.RejectionDiagnostics
        @ contract.FaultDiagnostics
        @ contract.RecoveryDiagnostics

    Expect.equal
        definitions.Length
        (definitions |> List.map _.Id |> Set.ofList |> Set.count)
        "No cross-family token collision"

    for inventory in [ contract.FaultDiagnostics; contract.RecoveryDiagnostics ] do
        Expect.isNonEmpty inventory "No vacuous qualification"

        Expect.isTrue
            (inventory |> List.forall (fun item -> item.Parameters.IsEmpty))
            "Exact parameterless inventory"

let private comparePayload id (response: CliWireResponse) web cliProperty =
    let native = outcome cliProperty response.Bytes
    let browser = outcome "data" web

    Expect.equal
        (native.GetRawText())
        (browser.GetRawText())
        "Both adapters project the same cause and guidance"

    let diagnostic = native.GetProperty("diagnostic")
    Expect.equal (diagnostic.GetProperty("id").GetString()) id "Explicit identity"

    Expect.equal
        (diagnostic.GetProperty("parameters").GetRawText())
        "{}"
        "No null, omitted or arbitrary arguments"

let private faultParity () =
    for fault, id in CoreFaults.all do
        let response = CliWireCodec.caseGet "case.get" (QueryOutcome.Failed fault)
        Expect.equal response.ExitCode 3 "Query failure exit remains three"
        comparePayload id response (WebWireCodec.get (QueryOutcome.Failed fault)) "fault"

let private recoveryParity () =
    for reason, id in RecoveryRejections.all do
        let value = RecoveryQueryOutcome.RecoveryRejected reason
        let response = CliWireCodec.recoveryList "recovery.list" value
        Expect.equal response.ExitCode 2 "Lifecycle refusal is not a fault or unknown commit"
        comparePayload id response (WebWireCodec.recoveryList value) "rejection"

let private localSeparation () =
    let coreIds = CoreFaults.all |> List.map snd |> Set.ofList

    for endpoint in (ContractProjection.current ()).CliEndpoints do
        for local in CliLocalFaults.all do
            let response = CliWireCodec.localFailure endpoint.Identifier local
            Expect.equal response.ExitCode 3 "Adapter exit preserved"

            Expect.equal
                (outcome "kind" response.Bytes |> _.GetString())
                "localFailure"
                "Never a fabricated core result"

            let fault = outcome "fault" response.Bytes

            Expect.equal
                (fault.GetProperty("recommendedAction").GetString())
                "STOP_AND_INVESTIGATE"
                "Local failure is not replay authority"

            Expect.isFalse
                (Set.contains (CliLocalFaults.token local) coreIds)
                "Separate transport vocabulary"

let private fingerprintChanges () =
    let baseline = SemanticContract.current
    let fingerprint = SemanticContract.fingerprint baseline

    let variants =
        [
            { baseline with
                FaultDiagnostics = baseline.FaultDiagnostics.Tail
            }
            { baseline with
                RecoveryDiagnostics = baseline.RecoveryDiagnostics.Tail
            }
            { baseline with
                FaultDiagnostics =
                    baseline.FaultDiagnostics
                    |> List.map (fun item -> { item with Id = item.Id + "_CHANGED" })
            }
        ]

    for changed in variants do
        Expect.notEqual
            (SemanticContract.fingerprint changed)
            fingerprint
            "All declared cause metadata participates in identity"

let private cultures () =
    let saved = CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture

    let values () =
        (CoreFaults.all
         |> List.map (fun (fault, _) ->
             (CliWireCodec.caseGet "case.get" (QueryOutcome.Failed fault)).Bytes))
        @ (RecoveryRejections.all
           |> List.map (fun (reason, _) ->
               WebWireCodec.recoveryList (RecoveryQueryOutcome.RecoveryRejected reason)))

    let expected = values ()

    try
        for locale in [ "en-US"; "lv-LV"; "tr-TR"; "ar-SA" ] do
            CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo locale
            CultureInfo.CurrentUICulture <- CultureInfo.GetCultureInfo locale

            Expect.equal
                (values ())
                expected
                "Locale cannot alter diagnostic meaning or default wire bytes"
    finally
        CultureInfo.CurrentCulture <- fst saved
        CultureInfo.CurrentUICulture <- snd saved

let tests =
    testList
        "typed core outcome diagnostics"
        [
            testCase
                "fault identities and guidance match the reviewed closed vocabulary"
                faultVocabulary
            testCase
                "recovery identities and guidance match the reviewed closed vocabulary"
                recoveryVocabulary
            testCase
                "core reasons cannot carry messages arbitrary parameters or writable guidance"
                closedReasons
            testCase
                "semantic diagnostic families are disjoint complete and parameterless"
                catalogueSeparation
            testCase "all native faults have identical CLI and Web diagnostics" faultParity
            testCase "all lifecycle refusals have identical CLI and Web diagnostics" recoveryParity
            testCase
                "adapter failures are distinct from core results on every CLI endpoint"
                localSeparation
            testCase
                "fault and recovery diagnostic inventories participate in semantic identity"
                fingerprintChanges
            testCase "ambient locale does not change core outcome diagnostics" cultures
        ]
