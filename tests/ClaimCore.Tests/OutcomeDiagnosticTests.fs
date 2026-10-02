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

let private remote endpoint (bytes: byte array) =
    use document = JsonDocument.Parse(ReadOnlyMemory bytes)
    CliRemoteWireCodec.result endpoint document.RootElement

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

let private comparePayload id (response: CliWireResponse) web =
    let native =
        (parsed response.Bytes).GetProperty("service").GetProperty("outcome").GetProperty("data")

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
        let response = WebWireCodec.get (QueryOutcome.Failed fault) |> remote "case.get"

        Expect.equal
            response.ExitCode
            (if fault.Action = RecommendedAction.RecoverExact then
                 4
             else
                 3)
            "Core-owned fault knowledge determines delivery exit"

        comparePayload id response (WebWireCodec.get (QueryOutcome.Failed fault))

let private recoveryParity () =
    for reason, id in RecoveryRejections.all do
        let value = RecoveryQueryOutcome.RecoveryRejected reason
        let response = WebWireCodec.recoveryList value |> remote "recovery.list"

        Expect.equal
            response.ExitCode
            (if reason = RecoveryRejection.SubmissionAlreadyStarted then
                 4
             else
                 2)
            "Refusal preserves exact recovery direction for an earlier started submission"

        comparePayload id response (WebWireCodec.recoveryList value)

let private localSeparation () =
    let coreIds = CoreFaults.all |> List.map snd |> Set.ofList

    let problems =
        [
            CliRemoteProblem.Configuration
            CliRemoteProblem.Authentication
            CliRemoteProblem.ServiceUnavailable
            CliRemoteProblem.ServiceReplyInvalid
            CliRemoteProblem.PrivateSource
            CliRemoteProblem.PrivateDestination
            CliRemoteProblem.DeliveryUnconfirmed
        ]

    for endpoint in (ContractProjection.current ()).CliEndpoints do
        for problem in problems do
            let response = CliRemoteWireCodec.localFailure endpoint.Identifier problem
            let root = parsed response.Bytes
            Expect.equal (root.GetProperty("kind").GetString()) "localFailure" "Adapter-only result"

            let uncertain = problem = CliRemoteProblem.DeliveryUnconfirmed
            Expect.equal response.ExitCode (if uncertain then 4 else 3) "Exact local exit"

            Expect.equal
                (root.GetProperty("action").GetString())
                (if uncertain then
                     "RECOVER_EXACT"
                 else
                     "STOP_AND_INVESTIGATE")
                "Local failure does not invent a core outcome"

            Expect.isFalse
                (Set.contains (root.GetProperty("code").GetString()) coreIds)
                "Separate vocabulary"

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
             (WebWireCodec.get (QueryOutcome.Failed fault) |> remote "case.get").Bytes))
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
