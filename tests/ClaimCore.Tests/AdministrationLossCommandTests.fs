module ClaimCore.Tests.AdministrationLossCommandTests

open System
open Expecto
open ClaimCore.Database

let private draft (first: Guid) (second: Guid) evidence known candidate =
    let arguments =
        [
            "draft-installation-loss-retirement"
            first.ToString("D")
            second.ToString("D")
            evidence
            "MISSING"
            known
            "KNOWN_OPERATIONS"
            candidate
        ]

    match DatabaseArguments.parse arguments with
    | Ok(DatabaseCommand.DraftInstallationLossRetirement(one,
                                                         two,
                                                         report,
                                                         checkpoint,
                                                         list,
                                                         mode,
                                                         path)) ->
        Expect.equal (one, two) (first, second) "Two distinct owner keys are required."

        Expect.equal
            (report, checkpoint, list, mode, path)
            (evidence, "MISSING", known, "KNOWN_OPERATIONS", candidate)
            "Only private evidence paths and a closed mode enter the draft."
    | _ -> failtest "Exact installation-loss draft was not admitted."

    let repeatedKey =
        arguments
        |> List.mapi (fun index value -> if index = 2 then first.ToString("D") else value)

    match DatabaseArguments.parse repeatedKey with
    | Error DatabaseInputProblem.UnsupportedInvocation -> ()
    | _ -> failtest "One signing key cannot approve both owners."

let private decisionPaths evidence known candidate =
    [
        candidate
        evidence
        "MISSING"
        known
        "/private/first-signature"
        "/private/second-signature"
    ]

let private record evidence known candidate =
    let paths = decisionPaths evidence known candidate

    match DatabaseArguments.parse ("retire-installation-after-loss" :: paths) with
    | Ok(DatabaseCommand.RetireInstallationAfterLoss value) ->
        Expect.equal value.Candidate candidate "Candidate stays private."

        Expect.equal
            (DatabaseArguments.commandToken (DatabaseCommand.RetireInstallationAfterLoss value))
            "RETIRE_INSTALLATION_AFTER_LOSS"
            "Retirement has a closed command token."
    | _ -> failtest "Owner loss-retirement invocation was not admitted."

    match DatabaseArguments.parse ("reconcile-installation-loss-retirement" :: paths) with
    | Ok(DatabaseCommand.ReconcileInstallationLossRetirement value) ->
        Expect.equal value.Candidate candidate "Readback binds the original candidate."
    | _ -> failtest "Owner loss-retirement reconciliation was not admitted."

let private diagnostic (candidate: string) =
    let rendered =
        DatabaseDiagnostics.inputFailure DatabaseInputProblem.InstallationLossFileRefused
        |> System.Text.Encoding.UTF8.GetString

    Expect.isFalse
        (rendered.Contains(candidate, StringComparison.Ordinal))
        "No private loss-evidence path enters diagnostics."

let private privateInvocation () =
    let first, second = Guid.NewGuid(), Guid.NewGuid()
    let evidence = "/private/loss-evidence"
    let known = "/private/known-operation-identities"
    let candidate = "/private/loss-decision"
    draft first second evidence known candidate
    record evidence known candidate
    diagnostic candidate

let cases =
    [
        testCase
            "[CC-BACKUP-001] installation loss retirement accepts only exact private evidence paths"
            privateInvocation
    ]
