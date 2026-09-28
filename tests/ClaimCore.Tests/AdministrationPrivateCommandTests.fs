module ClaimCore.Tests.AdministrationPrivateCommandTests

open System
open Expecto
open ClaimCore.Database

let private privatePurgeProposal () =
    let path = "/private/CLAIMANT-SENSITIVE-PROPOSAL"

    match DatabaseArguments.parse [ "purge-live"; path ] with
    | Ok(DatabaseCommand.PurgeLive value) ->
        Expect.equal value path "Only the opaque private-file path is parsed"

        Expect.equal
            (DatabaseArguments.commandToken (DatabaseCommand.PurgeLive value))
            "PURGE_LIVE"
            "Owner purge has a closed diagnostic command token"
    | _ -> failtest "Owner purge file invocation was not admitted"

    match DatabaseArguments.parse [ "purge-live"; path; "EXTRA" ] with
    | Error DatabaseInputProblem.UnsupportedInvocation -> ()
    | _ -> failtest "No free-form claimant or reason arguments may be accepted"

    match DatabaseArguments.parse [ "prune-witness-payload"; path ] with
    | Ok(DatabaseCommand.PruneWitnessPayload value) ->
        Expect.equal value path "Witness prune receives only a private proposal path"

        Expect.equal
            (DatabaseArguments.commandToken (DatabaseCommand.PruneWitnessPayload value))
            "PRUNE_WITNESS_PAYLOAD"
            "Witness prune has a closed owner command token"
    | _ -> failtest "Witness prune private proposal invocation was not admitted"

    match DatabaseArguments.parse [ "prune-witness-payload"; path; "EXTRA" ] with
    | Error DatabaseInputProblem.UnsupportedInvocation -> ()
    | _ -> failtest "Witness prune refuses raw claimant or reason arguments"

    let rendered =
        DatabaseDiagnostics.inputFailure DatabaseInputProblem.ErasureProposalFileRefused
        |> System.Text.Encoding.UTF8.GetString

    Expect.isFalse (rendered.Contains(path, StringComparison.Ordinal)) "Private path is not echoed"

let private writerAbortDraft candidate =
    let handoffId, keyOne, keyTwo = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()

    let draft =
        [
            "draft-writer-handoff-abort"
            handoffId.ToString("D")
            keyOne.ToString("D")
            keyTwo.ToString("D")
            candidate
        ]

    match DatabaseArguments.parse draft with
    | Ok(DatabaseCommand.DraftWriterHandoffAbort(id, one, two, path)) ->
        Expect.equal
            (id, one, two, path)
            (handoffId, keyOne, keyTwo, candidate)
            "Draft takes only handoff, signer-key identities and a new private output path."

        Expect.equal
            (DatabaseArguments.commandToken (
                DatabaseCommand.DraftWriterHandoffAbort(id, one, two, path)
            ))
            "DRAFT_WRITER_HANDOFF_ABORT"
            "Draft has a closed command token."
    | _ -> failtest "Owner abort draft invocation was not admitted."

    match
        DatabaseArguments.parse
            [
                "draft-writer-handoff-abort"
                handoffId.ToString("D")
                keyOne.ToString("D")
                keyOne.ToString("D")
                candidate
            ]
    with
    | Error DatabaseInputProblem.UnsupportedInvocation -> ()
    | _ -> failtest "One signer key cannot draft both owner approvals."

let private privateWriterAbort () =
    let candidate = "/private/ABORT-CANDIDATE"
    let first = "/private/OWNER-ONE-SIGNATURE"
    let second = "/private/OWNER-TWO-SIGNATURE"

    match DatabaseArguments.parse [ "abort-writer-handoff"; candidate; first; second ] with
    | Ok(DatabaseCommand.AbortWriterHandoff(actual, one, two)) ->
        Expect.equal
            (actual, one, two)
            (candidate, first, second)
            "Only three owner-private evidence paths enter the command."

        Expect.equal
            (DatabaseArguments.commandToken (DatabaseCommand.AbortWriterHandoff(actual, one, two)))
            "ABORT_WRITER_HANDOFF"
            "Abort has an exact closed command token."
    | _ -> failtest "Owner abort file invocation was not admitted."

    match
        DatabaseArguments.parse [ "abort-writer-handoff"; candidate; first; second; "ACTOR-UUID" ]
    with
    | Error DatabaseInputProblem.UnsupportedInvocation -> ()
    | _ -> failtest "An actor ID cannot be supplied to the owner abort command."

    let rendered =
        DatabaseDiagnostics.inputFailure DatabaseInputProblem.WriterHandoffFileRefused
        |> System.Text.Encoding.UTF8.GetString

    Expect.isFalse
        (rendered.Contains(candidate, StringComparison.Ordinal))
        "Private abort evidence path is never echoed."

    writerAbortDraft candidate

let private privateCopyAdoption () =
    let proposal = "/private/adoption-proposal"

    match DatabaseArguments.parse [ "adopt-managed-copy"; proposal ] with
    | Ok(DatabaseCommand.AdoptManagedCopy path) ->
        Expect.equal path proposal "Only one owner-private proposal path enters adoption."

        Expect.equal
            (DatabaseArguments.commandToken (DatabaseCommand.AdoptManagedCopy path))
            "ADOPT_MANAGED_COPY"
            "Adoption has an exact closed command token."
    | _ -> failtest "Owner adoption proposal invocation was not admitted."

    match DatabaseArguments.parse [ "adopt-managed-copy"; proposal; "CASE-UUID" ] with
    | Error DatabaseInputProblem.UnsupportedInvocation -> ()
    | _ -> failtest "A case ID cannot be supplied to owner adoption."

let private privateExternalCopyPublication () =
    let proposal = "/private/external-copy-proposal"

    match DatabaseArguments.parse [ "publish-external-copy"; proposal ] with
    | Ok(DatabaseCommand.PublishExternalCopy path) ->
        Expect.equal path proposal "Publication receives exactly the private proposal path."

        Expect.equal
            (DatabaseArguments.commandToken (DatabaseCommand.PublishExternalCopy path))
            "PUBLISH_EXTERNAL_COPY"
            "Publication has an exact closed command token."
    | _ -> failtest "External-copy publication proposal invocation was not admitted."

    for arguments in
        [
            [ "publish-external-copy" ]
            [ "publish-external-copy"; proposal; "CASE-UUID" ]
            [ "publish-external-copy"; proposal; "COPY-UUID"; "CUSTODIAN-UUID" ]
        ] do
        match DatabaseArguments.parse arguments with
        | Error DatabaseInputProblem.UnsupportedInvocation -> ()
        | _ -> failtest "Publication must reject missing or extra raw identifiers."

let private privateAdoptedCopyTransition () =
    let canonical = "/private/adopted-transition.json"
    let signature = "/private/adopted-transition.sig"

    for command, expected, inspect in
        [
            "transition-adopted-copy",
            "TRANSITION_ADOPTED_COPY",
            (fun value ->
                match value with
                | DatabaseCommand.TransitionAdoptedCopy(body, signed) ->
                    body = canonical && signed = signature
                | _ -> false)
            "verify-delete-adopted-copy",
            "VERIFY_DELETE_ADOPTED_COPY",
            (fun value ->
                match value with
                | DatabaseCommand.VerifyDeleteAdoptedCopy(body, signed) ->
                    body = canonical && signed = signature
                | _ -> false)
        ] do
        match DatabaseArguments.parse [ command; canonical; signature ] with
        | Ok parsed ->
            Expect.isTrue (inspect parsed) "Only exact private evidence paths are parsed"
            Expect.equal (DatabaseArguments.commandToken parsed) expected "Closed owner token"
        | _ -> failtest "Signed adopted-copy command was not admitted"

        match DatabaseArguments.parse [ command; canonical; signature; "CASE-UUID" ] with
        | Error DatabaseInputProblem.UnsupportedInvocation -> ()
        | _ -> failtest "Signed adopted-copy command rejected an extra raw case identity"

let private privateManagedPayloadAbsence () =
    let proposal = "/private/managed-payload-absence-proposal"

    match DatabaseArguments.parse [ "certify-managed-payload-absence"; proposal ] with
    | Ok(DatabaseCommand.CertifyManagedPayloadAbsence path) ->
        Expect.equal path proposal "Only the private proposal path is admitted"

        Expect.equal
            (DatabaseArguments.commandToken (DatabaseCommand.CertifyManagedPayloadAbsence path))
            "CERTIFY_MANAGED_PAYLOAD_ABSENCE"
            "Terminal copy absence has one closed command token"
    | _ -> failtest "Terminal copy absence proposal invocation was not admitted"

    for arguments in
        [
            [ "certify-managed-payload-absence" ]
            [ "certify-managed-payload-absence"; proposal; "CASE-UUID" ]
        ] do
        match DatabaseArguments.parse arguments with
        | Error DatabaseInputProblem.UnsupportedInvocation -> ()
        | _ -> failtest "Terminal copy absence refuses missing or extra raw identifiers"

let private privateBackupCapture () =
    match DatabaseArguments.parse [ "hold-backup-capture" ] with
    | Ok DatabaseCommand.HoldBackupCapture ->
        Expect.equal
            (DatabaseArguments.commandToken DatabaseCommand.HoldBackupCapture)
            "HOLD_BACKUP_CAPTURE"
            "Backup capture has one closed owner command token."
    | _ -> failtest "Owner-held backup capture command was not admitted."

    for arguments in
        [
            [ "hold-backup-capture"; "/private/cycle" ]
            [ "hold-backup-capture"; "CASE-UUID" ]
        ] do
        match DatabaseArguments.parse arguments with
        | Error DatabaseInputProblem.UnsupportedInvocation -> ()
        | _ -> failtest "Backup capture refuses all owner-selected arguments."

    let lease = Guid.NewGuid()

    match DatabaseArguments.parse [ "reconcile-backup-capture"; lease.ToString("D") ] with
    | Ok(DatabaseCommand.ReconcileBackupCapture value) ->
        Expect.equal value lease "Readback is limited to one opaque captured lease."

        Expect.equal
            (DatabaseArguments.commandToken (DatabaseCommand.ReconcileBackupCapture value))
            "RECONCILE_BACKUP_CAPTURE"
            "Readback has a closed diagnostic command token."
    | _ -> failtest "Exact backup capture readback was not admitted."

    match DatabaseArguments.parse [ "reconcile-backup-capture"; "CASE-UUID" ] with
    | Error DatabaseInputProblem.UnsupportedInvocation -> ()
    | _ -> failtest "Readback refuses noncanonical or claimant-bearing identities."

let private privateBackupHealthReadback () =
    let policy, source, output =
        "/private/policy.json", "/private/cycle.source.json", "/private/health.json"

    match DatabaseArguments.parse [ "reconcile-backup-health"; policy; source; output ] with
    | Ok(DatabaseCommand.ReconcileBackupHealth(actualPolicy, actualSource, actualOutput)) ->
        Expect.equal
            (actualPolicy, actualSource, actualOutput)
            (policy, source, output)
            "Historical readback takes only exact private policy, source and output paths."

        Expect.equal
            (DatabaseArguments.commandToken (
                DatabaseCommand.ReconcileBackupHealth(policy, source, output)
            ))
            "RECONCILE_BACKUP_HEALTH"
            "Historical health readback has one closed command token."
    | _ -> failtest "Historical backup health readback was not admitted."

    match
        DatabaseArguments.parse [ "reconcile-backup-health"; policy; source; output; "CASE-UUID" ]
    with
    | Error DatabaseInputProblem.UnsupportedInvocation -> ()
    | _ -> failtest "Historical health readback refuses a raw case identifier."

let cases =
    [
        testCase
            "[CC-ERASE-001] owner purge and prune accept only private proposal path"
            privatePurgeProposal
        testCase
            "[CC-BACKUP-001] owner abort accepts only signed private evidence paths"
            privateWriterAbort
        testCase
            "[CC-BACKUP-001] owner adoption accepts only one private evidence path"
            privateCopyAdoption
        testCase
            "[CC-BACKUP-001] external copy publication accepts only one private proposal path"
            privateExternalCopyPublication
        testCase
            "[CC-BACKUP-001] adopted copy transitions accept only signed private evidence paths"
            privateAdoptedCopyTransition
        testCase
            "[CC-ERASE-001] terminal copy absence accepts only one private proposal path"
            privateManagedPayloadAbsence
        testCase
            "[CC-BACKUP-001] owner backup capture accepts no path or caller-selected executable"
            privateBackupCapture
        testCase
            "[CC-BACKUP-001] historical health readback accepts only owner-private evidence paths"
            privateBackupHealthReadback
    ]
