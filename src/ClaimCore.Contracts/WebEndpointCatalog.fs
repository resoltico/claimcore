namespace ClaimCore.Contracts

open ClaimCore.Application

module internal WebEndpointCatalog =
    let private sourceDigestHeader =
        [ "X-ClaimCore-Source-Sha256", EndpointInputs.digest ]

    let private endpoint responses identifier method path body successMediaType =
        {
            Identifier = identifier
            Method = method
            Path = path
            Body = body
            Response = Map.find identifier responses
            SuccessMediaType = successMediaType
        }

    let private json input = Some(JsonBody input)

    let private raw mediaType maximumBytes headers =
        Some(RawBody(mediaType, maximumBytes, headers))

    let private sessions responses =
        [
            endpoint responses "session" "GET" "/api/v3/session" None None
            endpoint
                responses
                "session.logout"
                "POST"
                "/api/v3/session/logout"
                (json (Schema.objectOf false []))
                None
            endpoint responses "definition" "GET" "/api/v3/definition" None None
        ]

    let private cases responses (semantic: SemanticCoreContract) =
        [
            endpoint
                responses
                "case.get"
                "POST"
                "/api/v3/cases/get"
                (json (EndpointInputs.caseReference semantic))
                None
            endpoint
                responses
                "case.list"
                "POST"
                "/api/v3/cases/list"
                (json (EndpointInputs.cursor semantic.MaximumPageSize))
                None
            endpoint
                responses
                "case.history"
                "POST"
                "/api/v3/cases/history"
                (json (EndpointInputs.history semantic))
                None
            endpoint
                responses
                "operation.observe"
                "POST"
                "/api/v3/operations/observe"
                (json EndpointInputs.operation)
                None
            endpoint
                responses
                "command.prepare"
                "POST"
                "/api/v3/operations/prepare"
                (json (ProjectionSchema.commandDraft semantic))
                None
            endpoint
                responses
                "command.execute"
                "POST"
                "/api/v3/operations/submit"
                (json (ProjectionSchema.commandDraft semantic))
                None
        ]

    let private recoveryJson responses (semantic: SemanticCoreContract) =
        [
            endpoint
                responses
                "recovery.list"
                "POST"
                "/api/v3/recovery/list"
                (json (EndpointInputs.recoveryList semantic.MaximumPageSize))
                None
            endpoint
                responses
                "recovery.inspect"
                "POST"
                "/api/v3/recovery/inspect"
                (json (EndpointInputs.recoveryInspect semantic.MaximumPageSize))
                None
            endpoint
                responses
                "recovery.resolve"
                "POST"
                "/api/v3/recovery/resolve"
                (json EndpointInputs.recoveryResolution)
                None
            endpoint
                responses
                "recovery.dismiss"
                "POST"
                "/api/v3/recovery/dismiss"
                (json EndpointInputs.recoveryDismiss)
                None
            endpoint
                responses
                "recovery.export"
                "POST"
                "/api/v3/recovery/export"
                (json EndpointInputs.recoveryExportTarget)
                (Some "application/vnd.claimcore.recovery+json")
        ]

    let private recoveryRaw responses =
        [
            endpoint
                responses
                "recovery.importEnvelopePreview"
                "POST"
                "/api/v3/recovery/import-envelope/preview"
                (raw "application/vnd.claimcore.recovery+json" 131072 [])
                None
            endpoint
                responses
                "recovery.importEnvelopeRetain"
                "POST"
                "/api/v3/recovery/import-envelope/retain"
                (raw "application/vnd.claimcore.recovery+json" 131072 sourceDigestHeader)
                None
        ]

    let private authorityManagement responses =
        [
            endpoint
                responses
                "authority.register"
                "POST"
                "/api/v3/authority/register"
                (json WebManagementSchemas.register)
                None
            endpoint
                responses
                "authority.setGrant"
                "POST"
                "/api/v3/authority/grants/set"
                (json WebManagementSchemas.setGrant)
                None
            endpoint
                responses
                "authority.setEnabled"
                "POST"
                "/api/v3/authority/actors/enable"
                (json WebManagementSchemas.setEnabled)
                None
            endpoint
                responses
                "authority.observe"
                "POST"
                "/api/v3/authority/observe"
                (json WebManagementSchemas.observe)
                None
        ]

    let private authorityApprovals responses =
        [
            endpoint
                responses
                "authority.approveCopySigner"
                "POST"
                "/api/v3/authority/copy-signers/approve"
                (json WebSignerApprovalSchemas.request)
                None
            endpoint
                responses
                "authority.approveCopyDeletion"
                "POST"
                "/api/v3/authority/copies/deletion/approve"
                (json WebCopyDeletionApprovalSchemas.request)
                None
            endpoint
                responses
                "authority.approveCopyAdoption"
                "POST"
                "/api/v3/authority/copies/adoption/approve"
                (json WebCopyAdoptionApprovalSchemas.request)
                None
            endpoint
                responses
                "authority.approveWriterHandoff"
                "POST"
                "/api/v3/authority/writer-handoffs/approve"
                (json WebWriterHandoffApprovalSchemas.request)
                None
            endpoint
                responses
                "authority.reviewRealDataActivation"
                "POST"
                "/api/v3/authority/real-data-activation/review"
                (json WebRealDataActivationSchemas.reviewRequest)
                None
            endpoint
                responses
                "authority.approveRealDataActivation"
                "POST"
                "/api/v3/authority/real-data-activation/approve"
                (json WebRealDataActivationSchemas.approvalRequest)
                None
        ]

    let private authority responses =
        authorityManagement responses @ authorityApprovals responses

    let private lifecycle responses =
        [
            endpoint
                responses
                "lifecycle.review"
                "POST"
                "/api/v3/lifecycle/review"
                (json WebLifecycleSchemas.reviewInput)
                None
            endpoint
                responses
                "lifecycle.apply"
                "POST"
                "/api/v3/lifecycle/apply"
                (json WebLifecycleSchemas.applyInput)
                None
            endpoint
                responses
                "lifecycle.approve"
                "POST"
                "/api/v3/lifecycle/approve"
                (json WebLifecycleSchemas.approveInput)
                None
        ]

    let private tombstones responses =
        [
            endpoint
                responses
                "tombstone.review"
                "POST"
                "/api/v3/tombstones/review"
                (json WebTombstoneSchemas.reviewInput)
                None
            endpoint
                responses
                "tombstone.approvePrune"
                "POST"
                "/api/v3/tombstones/prune/approve"
                (json WebTombstoneSchemas.approveInput)
                None
            endpoint
                responses
                "tombstone.approveTerminal"
                "POST"
                "/api/v3/tombstones/terminal/approve"
                (json WebTerminalApprovalSchemas.request)
                None
            endpoint
                responses
                "tombstone.changeHold"
                "POST"
                "/api/v3/tombstones/holds/change"
                (json WebTombstoneSchemas.holdInput)
                None
        ]

    let all (semantic: SemanticCoreContract) =
        let responses = WebResponseCatalog.all () |> Map.ofList

        sessions responses
        @ cases responses semantic
        @ recoveryJson responses semantic
        @ recoveryRaw responses
        @ authority responses
        @ lifecycle responses
        @ tombstones responses
