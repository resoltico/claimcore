module ClaimCore.IntegrationTests.ActorAdviceTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private ct = CancellationToken.None

let private appliedActor =
    function
    | ActorManagementOutcome.Applied _ -> ()
    | _ -> failtest "Synthetic authenticated authority event must be confirmed."

let private withActorFixture action =
    withAuthorityRuntimeDatabase (fun ownerConnection app writer witness ->
        let owner = human "advice-owner"
        provision ownerConnection witness owner |> applied
        use source = RuntimeDataSource.create app
        let registry = new ActorGrantRegistry(source, witness)
        let ownerId = actorId (new ActorGrantStore(source)) owner

        registry.SetGrant(
            owner,
            ownerId,
            {
                Role = Role.CaseEditor
                Scope = GrantScope.Installation
            },
            true
        )
        |> await
        |> applied

        use runtime =
            Runtime.OpenPostgres(
                app,
                writer,
                witnessKey (),
                suppressionKeyFile (),
                artifactKeyRingFile (),
                ct
            )
            |> await
            |> accepted

        let core = runtime.ForActor owner
        let management = core.Management

        let register subject grants =
            let principal = human subject
            management.RegisterActor(Guid.NewGuid(), principal, ct) |> await |> appliedActor

            for role, scope in grants do
                management.SetGrant(Guid.NewGuid(), principal, role, scope, true, ct)
                |> await
                |> appliedActor

            principal

        let create reference =
            match core.Execute(openRequest (Guid.NewGuid()) reference, ct) |> await with
            | SubmissionOutcome.Completed(_,
                                          _,
                                          DefiniteExecution.Accepted _,
                                          SettlementConfirmation.Confirmed) -> ()
            | _ -> failtest "Synthetic case seed must be accepted once."

        create "ADVICE-A"
        create "ADVICE-B"
        action runtime core register)

let private commandScenarios =
    let installation = GrantTarget.Installation
    let scoped = GrantTarget.CaseReference "ADVICE-A"

    [
        "installation-reader", [ Role.CaseReader, installation ], true, false, false
        "installation-editor", [ Role.CaseEditor, installation ], true, true, true
        "case-reader", [ Role.CaseReader, scoped ], true, false, false
        "case-editor", [ Role.CaseEditor, scoped ], true, true, false
        "owner-only", [ Role.Owner, installation ], false, false, false
        "recovery-only", [ Role.RecoveryOperator, installation ], false, false, false
        "exporter-only", [ Role.RecoveryExporter, installation ], false, false, false
        "steward-only", [ Role.DataSteward, installation ], false, false, false
        "no-grants", [], false, false, false
    ]

let private verifyReadAdvice (core: IActorClaimsCore) readable editable =
    match core.Get("ADVICE-A", ct) |> await with
    | QueryOutcome.Succeeded(Lookup.Found current) when readable ->
        Expect.equal
            (current.AvailableCommands |> List.map CommandKinds.token |> Set.ofList)
            (if editable then
                 set [ "AMEND_REGISTRATION"; "DECIDE"; "CLOSE" ]
             else
                 Set.empty)
            "Advice combines current state with this actor's case authority"
    | QueryOutcome.Rejected Rejection.ResourceUnavailable when not readable -> ()
    | _ -> failtest "Read authority cannot be inferred from another role."

let private commandAdvice =
    testCase
        "[CC-AUTH-001] actor command advice preserves narrow scopes without wider grants"
        (fun () ->
            withActorFixture (fun runtime _ register ->
                for subject, grants, readable, editable, mayOpen in commandScenarios do
                    let principal = register subject grants
                    let core = runtime.ForActor principal

                    verifyReadAdvice core readable editable

                    match core.List({ AfterCursor = None; Limit = 10 }, ct) |> await with
                    | QueryOutcome.Succeeded page ->
                        Expect.equal
                            page.AvailableCommands
                            (if mayOpen then [ CommandKind.Open ] else [])
                            "Only installation editing permits OPEN advice"

                        Expect.equal
                            page.Items.Length
                            (if not readable then
                                 0
                             elif subject.StartsWith("case-", StringComparison.Ordinal) then
                                 1
                             else
                                 2)
                            "Case-scoped list filtering remains intact"
                    | _ -> failtest "An enabled actor retains bounded filtered listing."

                    if subject.StartsWith("case-", StringComparison.Ordinal) then
                        match core.Get("ADVICE-B", ct) |> await with
                        | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
                        | _ -> failtest "Advice must not widen an unrelated case grant."))

let private inspection (core: IActorClaimsCore) operationId =
    match core.Recovery.Inspect(operationId, None, 10, ct) |> await with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection detail)) ->
        detail
    | _ -> failtest "Granted exact operation inspection must succeed."

let private prepared (core: IActorClaimsCore) reference =
    let input =
        { openRequest (Guid.NewGuid()) reference with
            ExpectedVersion = 1L
            Command = Command.Close
        }

    match core.Prepare(input, ct) |> await with
    | PrepareOutcome.Prepared(details, _) ->
        input.OperationId,
        details.Summary.RequestSha256
        |> Option.defaultWith (fun () -> failtest "Exact preparation digest is required.")
    | _ -> failtest "Synthetic CLOSE must be prepared without acceptance."

let private verifyExportOnly (exporter: IActorClaimsCore) operationId digest =
    match exporter.Recovery.Inspect(operationId, None, 10, ct) |> await with
    | RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable -> ()
    | _ -> failtest "Export permission must not imply inspection."

    match exporter.Recovery.ExportEnvelope(operationId, digest, ct) |> await with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found envelope) ->
        Expect.equal envelope.RequestSha256 digest "Known-ID export preserves exact identity"

        Expect.isGreaterThan envelope.Bytes.Length 0 "Authorized envelope was issued"
    | _ -> failtest "Exporter must work without list or inspect authority."

let private verifyResolvedAdvice
    (operator: IActorClaimsCore)
    (combined: IActorClaimsCore)
    operationId
    digest
    =
    match operator.Recovery.Resolve(operationId, digest, ct) |> await with
    | ResolveOutcome.ResolveCompleted(_,
                                      _,
                                      DefiniteExecution.Accepted _,
                                      SettlementConfirmation.Confirmed) -> ()
    | _ -> failtest "Case-scoped recovery may resolve the retained request without editing grants."

    Expect.isEmpty
        (inspection operator operationId).Preparation.Summary.AvailableActions
        "Accepted state cannot regain Resolve or Dismiss"

    Expect.equal
        (inspection combined operationId).Preparation.Summary.AvailableActions
        [ RecoveryAction.Export ]
        "Accepted inspection offers only authorized export"

let private recoveryAdvice =
    testCase
        "[CC-REC-001] inspection advice distinguishes recovery and export without granting either"
        (fun () ->
            withActorFixture (fun runtime owner register ->
                let scoped = GrantTarget.CaseReference "ADVICE-A"

                let operator =
                    register "scoped-recovery" [ Role.RecoveryOperator, scoped ]
                    |> runtime.ForActor

                let exporter =
                    register "scoped-exporter" [ Role.RecoveryExporter, scoped ]
                    |> runtime.ForActor

                let combined =
                    register
                        "combined-recovery"
                        [
                            Role.RecoveryOperator, GrantTarget.Installation
                            Role.RecoveryExporter, GrantTarget.Installation
                        ]
                    |> runtime.ForActor

                let operationId, digest = prepared owner "ADVICE-A"

                Expect.equal
                    (inspection operator operationId).Preparation.Summary.AvailableActions
                    [ RecoveryAction.Resolve; RecoveryAction.Dismiss ]
                    "Operator inspection does not advertise export"

                Expect.equal
                    (inspection combined operationId).Preparation.Summary.AvailableActions
                    [ RecoveryAction.Resolve; RecoveryAction.Dismiss; RecoveryAction.Export ]
                    "Combined grants preserve every state-permitted action"

                verifyExportOnly exporter operationId digest

                verifyResolvedAdvice operator combined operationId digest

                let revokedId, revokedDigest = prepared owner "ADVICE-B"

                match combined.Recovery.Dismiss(revokedId, revokedDigest, true, ct) |> await with
                | RecoveryDismissOutcome.DismissedPreparation _ -> ()
                | _ ->
                    failtest "Explicit dismissal must revoke only the unaccepted exact identity."

                Expect.equal
                    (inspection combined revokedId).Preparation.Summary.AvailableActions
                    [ RecoveryAction.Export ]
                    "Revocation never regains recording authority"))

let private inactiveAdvice =
    testCase "[CC-AUTH-001] disabled and unregistered actors gain no case or list advice" (fun () ->
        withActorFixture (fun runtime owner register ->
            let disabled =
                register "disabled-reader" [ Role.CaseReader, GrantTarget.Installation ]

            owner.Management.SetEnabled(Guid.NewGuid(), disabled, false, ct)
            |> await
            |> appliedActor

            for principal in [ disabled; human "unregistered-advice" ] do
                let core = runtime.ForActor principal

                match core.Get("ADVICE-A", ct) |> await with
                | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
                | _ ->
                    failtest
                        "Inactive identity cannot disclose current state or advisory commands."

                match core.List({ AfterCursor = None; Limit = 10 }, ct) |> await with
                | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
                | _ -> failtest "Inactive identity cannot disclose list rows or OPEN advice."))

let tests =
    testList "actor resource advice" [ commandAdvice; recoveryAdvice; inactiveAdvice ]
