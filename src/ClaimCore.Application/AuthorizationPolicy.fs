namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

type internal IActorGrantSource =
    abstract LoadForScope:
        principal: PrincipalKey * resource: ResourceScope * cancellationToken: CancellationToken ->
            Task<ActorAuthority option>

module ActorAuthorization =
    let private capabilityPairs =
        [
            EndpointAction.Definition, Capability.ReadDefinition
            EndpointAction.ListCases, Capability.ListCases
            EndpointAction.GetCase, Capability.ReadCase
            EndpointAction.HistorySummary, Capability.ReadHistorySummary
            EndpointAction.HistoryFull, Capability.ReadHistoryFull
            EndpointAction.ObserveOperation, Capability.ObserveOperation
            EndpointAction.PrepareNewCase, Capability.EditCase
            EndpointAction.ExecuteNewCase, Capability.EditCase
            EndpointAction.PrepareCommand, Capability.EditCase
            EndpointAction.ExecuteCommand, Capability.EditCase
            EndpointAction.RecoveryList, Capability.ListRecovery
            EndpointAction.RecoveryInspect, Capability.InspectRecovery
            EndpointAction.RecoveryResolve, Capability.ResolveRecovery
            EndpointAction.RecoveryDismiss, Capability.DismissRecovery
            EndpointAction.RecoveryExport, Capability.ExportRecovery
            EndpointAction.RecoveryImportPreview, Capability.PreviewRecoveryImport
            EndpointAction.RecoveryImportRetain, Capability.RetainRecoveryImport
            EndpointAction.ManageGrants, Capability.ManageGrants
            EndpointAction.ManageHolds, Capability.ManageHolds
            EndpointAction.ReviewLifecycle, Capability.ReviewLifecycle
            EndpointAction.VoidCase, Capability.VoidCase
            EndpointAction.ReinstateCase, Capability.ReinstateCase
            EndpointAction.RequestErasure, Capability.RequestErasure
            EndpointAction.ApproveLifecycle, Capability.ApproveLifecycle
            EndpointAction.ApproveErasure, Capability.ApproveErasure
            EndpointAction.ReviewTombstone, Capability.ReviewTombstone
            EndpointAction.ManageTombstoneHold, Capability.ManageTombstoneHold
            EndpointAction.ApproveWitnessPrune, Capability.ApproveWitnessPrune
            EndpointAction.ApproveTerminalErasure, Capability.ApproveTerminalErasure
            EndpointAction.VerifyData, Capability.VerifyData
            EndpointAction.BackupReport, Capability.ReadBackupReport
            EndpointAction.ApproveRestore, Capability.ApproveRestore
            EndpointAction.ApproveCopySigner, Capability.ApproveCopySigner
            EndpointAction.ApproveCopyDeletion, Capability.ApproveCopyDeletion
            EndpointAction.ApproveCopyAdoption, Capability.ApproveCopyAdoption
            EndpointAction.ApproveWriterHandoff, Capability.ApproveWriterHandoff
            EndpointAction.ApproveRealDataActivation, Capability.ApproveRealDataActivation
            EndpointAction.ReviewRealDataActivation, Capability.ReviewRealDataActivation
        ]

    let allActions = capabilityPairs |> List.map fst
    let private capabilityMap = capabilityPairs |> Map.ofList

    let commandCapability =
        function
        | CommandKind.Open
        | CommandKind.AmendRegistration
        | CommandKind.CorrectCase
        | CommandKind.Decide
        | CommandKind.WithdrawDecision
        | CommandKind.RecordPayment
        | CommandKind.ClearPayment
        | CommandKind.Close
        | CommandKind.Reopen -> Capability.EditCase

    let requiredCapability action = Map.tryFind action capabilityMap

    let private installationActions =
        set
            [
                EndpointAction.Definition
                EndpointAction.ListCases
                EndpointAction.PrepareNewCase
                EndpointAction.ExecuteNewCase
                EndpointAction.RecoveryList
                EndpointAction.RecoveryImportPreview
                EndpointAction.ManageGrants
                EndpointAction.VerifyData
                EndpointAction.BackupReport
                EndpointAction.ApproveRestore
                EndpointAction.ApproveCopySigner
                EndpointAction.ApproveCopyDeletion
                EndpointAction.ApproveWriterHandoff
                EndpointAction.ApproveRealDataActivation
                EndpointAction.ReviewRealDataActivation
            ]

    let private installationAction action = Set.contains action installationActions

    let private operationAction =
        function
        | EndpointAction.ObserveOperation
        | EndpointAction.RecoveryInspect
        | EndpointAction.RecoveryResolve
        | EndpointAction.RecoveryDismiss
        | EndpointAction.RecoveryExport -> true
        | _ -> false

    let private validTarget action resource =
        match resource with
        | ResourceScope.Installation -> installationAction action
        | ResourceScope.Operation(operationId, caseId) ->
            operationAction action && operationId <> Guid.Empty && caseId <> Guid.Empty
        | ResourceScope.Case caseId ->
            caseId <> Guid.Empty
            && not (installationAction action || operationAction action)

    let private ownerCapabilities =
        set
            [
                Capability.ReadDefinition
                Capability.ManageGrants
                Capability.ReadBackupReport
                Capability.ApproveCopySigner
                Capability.ApproveCopyAdoption
                Capability.ApproveWriterHandoff
                Capability.ApproveRealDataActivation
                Capability.ReviewRealDataActivation
            ]

    let private readerCapabilities =
        set
            [
                Capability.ReadDefinition
                Capability.ListCases
                Capability.ReadCase
                Capability.ReadHistorySummary
                Capability.ObserveOperation
            ]

    let private recoveryCapabilities =
        set
            [
                Capability.ListRecovery
                Capability.InspectRecovery
                Capability.ResolveRecovery
                Capability.DismissRecovery
                Capability.PreviewRecoveryImport
                Capability.RetainRecoveryImport
            ]

    let private auditorCapabilities =
        set
            [
                Capability.ReadDefinition
                Capability.ReadCase
                Capability.ReadHistorySummary
                Capability.ReadHistoryFull
                Capability.ObserveOperation
                Capability.InspectRecovery
                Capability.VerifyData
                Capability.ReadBackupReport
                Capability.ApproveCopySigner
                Capability.ApproveCopyDeletion
            ]

    let private stewardCapabilities =
        set
            [
                Capability.ManageHolds
                Capability.ReviewLifecycle
                Capability.VoidCase
                Capability.ReinstateCase
                Capability.RequestErasure
                Capability.ApproveLifecycle
                Capability.ApproveErasure
                Capability.ReviewTombstone
                Capability.ManageTombstoneHold
                Capability.ApproveWitnessPrune
                Capability.ApproveTerminalErasure
                Capability.ApproveRestore
                Capability.ApproveCopySigner
                Capability.ApproveCopyDeletion
            ]

    let private roleCapabilities =
        function
        | Role.Owner -> ownerCapabilities
        | Role.CaseReader -> readerCapabilities
        | Role.CaseEditor ->
            readerCapabilities
            |> Set.add Capability.EditCase
            |> Set.add Capability.ReadHistoryFull
        | Role.RecoveryOperator -> recoveryCapabilities
        | Role.RecoveryExporter -> Set.singleton Capability.ExportRecovery
        | Role.AuditorCustodian -> auditorCapabilities
        | Role.DataSteward -> stewardCapabilities

    let internal rolesForCapability capability =
        [
            Role.Owner
            Role.CaseReader
            Role.CaseEditor
            Role.RecoveryOperator
            Role.RecoveryExporter
            Role.AuditorCustodian
            Role.DataSteward
        ]
        |> List.filter (fun role -> Set.contains capability (roleCapabilities role))

    let private grantCovers resource scope =
        match scope, resource with
        | GrantScope.Installation, _ -> true
        | GrantScope.Case granted, ResourceScope.Case target -> granted = target
        | GrantScope.Case granted, ResourceScope.Operation(_, target) -> granted = target
        | _ -> false

    let private humanActions =
        set
            [
                EndpointAction.ManageGrants
                EndpointAction.ManageHolds
                EndpointAction.ReviewLifecycle
                EndpointAction.VoidCase
                EndpointAction.ReinstateCase
                EndpointAction.RequestErasure
                EndpointAction.ApproveLifecycle
                EndpointAction.ApproveErasure
                EndpointAction.ReviewTombstone
                EndpointAction.ManageTombstoneHold
                EndpointAction.ApproveWitnessPrune
                EndpointAction.ApproveTerminalErasure
                EndpointAction.ApproveRestore
                EndpointAction.ApproveCopySigner
                EndpointAction.ApproveCopyDeletion
                EndpointAction.ApproveCopyAdoption
                EndpointAction.ApproveWriterHandoff
                EndpointAction.ApproveRealDataActivation
                EndpointAction.ReviewRealDataActivation
            ]

    let private humanRequired action = Set.contains action humanActions

    let authorize principal (authority: ActorAuthority) action resource =
        if
            authority.ActorId = Guid.Empty
            || authority.Principal <> principal
            || not authority.Enabled
            || authority.GrantRevision < 0L
            || (humanRequired action && not (PrincipalKey.isHuman principal))
            || not (validTarget action resource)
        then
            AuthorizationDecision.Unavailable
        else
            match requiredCapability action with
            | None -> AuthorizationDecision.Unavailable
            | Some capability ->
                if
                    authority.Grants
                    |> List.exists (fun grant ->
                        grantCovers resource grant.Scope
                        && Set.contains capability (roleCapabilities grant.Role))
                then
                    AuthorizationDecision.Available(authority.ActorId, authority.GrantRevision)
                else
                    AuthorizationDecision.Unavailable

    let authorizeAtRevision
        principal
        (authority: ActorAuthority)
        expectedGrantRevision
        action
        resource
        =
        if authority.GrantRevision <> expectedGrantRevision then
            AuthorizationDecision.Unavailable
        else
            authorize principal authority action resource

    let authorizeLookup principal authority action caseId =
        match caseId with
        | None -> AuthorizationDecision.Unavailable
        | Some value -> authorize principal authority action (ResourceScope.Case value)

    let authorizeOperationLookup principal authority action operation =
        match operation with
        | None -> AuthorizationDecision.Unavailable
        | Some(operationId, caseId) ->
            authorize principal authority action (ResourceScope.Operation(operationId, caseId))

    let can principal authority action resource =
        match authorize principal authority action resource with
        | AuthorizationDecision.Available _ -> true
        | AuthorizationDecision.Unavailable -> false
