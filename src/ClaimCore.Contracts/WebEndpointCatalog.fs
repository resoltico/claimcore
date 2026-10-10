namespace ClaimCore.Contracts

open ClaimCore.Application

module internal WebEndpointCatalog =
    let private sourceDigestHeader =
        [ "X-ClaimCore-Source-Sha256", EndpointInputs.digest ]

    let private endpoint response identifier method path body successMediaType =
        {
            Identifier = identifier
            Method = method
            Path = path
            Body = body
            Response = response
            SuccessMediaType = successMediaType
        }

    let private json input = Some(JsonBody input)

    let private postJson response identifier path input =
        endpoint response identifier "POST" path (json input) None

    let private raw mediaType maximumBytes headers =
        Some(RawBody(mediaType, maximumBytes, headers))

    let private sessions () =
        [
            endpoint
                (WebResponseSchemas.session "session")
                "session"
                "GET"
                "/api/v3/session"
                None
                None
            postJson
                (WebResponseSchemas.session "session.logout")
                "session.logout"
                "/api/v3/session/logout"
                (Schema.objectOf false [])
            endpoint
                (WebResponseSchemas.definition)
                "definition"
                "GET"
                "/api/v3/definition"
                None
                None
        ]

    let private cases (semantic: SemanticCoreContract) =
        [
            postJson
                (WebResponseSchemas.caseGet)
                "case.get"
                "/api/v3/cases/get"
                (EndpointInputs.caseReference semantic)
            postJson
                (WebResponseSchemas.caseList)
                "case.list"
                "/api/v3/cases/list"
                (EndpointInputs.cursor semantic.MaximumPageSize)
            postJson
                (WebResponseSchemas.history)
                "case.history"
                "/api/v3/cases/history"
                (EndpointInputs.history semantic)
            postJson
                (WebResponseSchemas.observe)
                "operation.observe"
                "/api/v3/operations/observe"
                EndpointInputs.operation
            postJson
                (WebResponseSchemas.prepare)
                "command.prepare"
                "/api/v3/operations/prepare"
                (ProjectionSchema.commandDraft semantic)
            postJson
                (WebResponseSchemas.submission)
                "command.execute"
                "/api/v3/operations/submit"
                (ProjectionSchema.commandDraft semantic)
        ]

    let private recoveryJson (semantic: SemanticCoreContract) =
        [
            postJson
                (WebResponseSchemas.recoveryList)
                "recovery.list"
                "/api/v3/recovery/list"
                (EndpointInputs.recoveryList semantic.MaximumPageSize)
            postJson
                (WebResponseSchemas.recoveryInspect)
                "recovery.inspect"
                "/api/v3/recovery/inspect"
                (EndpointInputs.recoveryInspect semantic.MaximumPageSize)
            postJson
                (WebResponseSchemas.resolve "recovery.resolve")
                "recovery.resolve"
                "/api/v3/recovery/resolve"
                EndpointInputs.recoveryResolution
            postJson
                (WebResponseSchemas.dismiss)
                "recovery.dismiss"
                "/api/v3/recovery/dismiss"
                EndpointInputs.recoveryDismiss
            endpoint
                (WebResponseSchemas.export)
                "recovery.export"
                "POST"
                "/api/v3/recovery/export"
                (json EndpointInputs.recoveryExportTarget)
                (Some "application/vnd.claimcore.recovery+json")
        ]

    let private recoveryRaw () =
        [
            endpoint
                (WebResponseSchemas.importPreview "recovery.importEnvelopePreview")
                "recovery.importEnvelopePreview"
                "POST"
                "/api/v3/recovery/import-envelope/preview"
                (raw
                    "application/vnd.claimcore.recovery+json"
                    TransportLimits.RecoveryArtifactBytes
                    [])
                None
            endpoint
                (WebResponseSchemas.importRetain "recovery.importEnvelopeRetain")
                "recovery.importEnvelopeRetain"
                "POST"
                "/api/v3/recovery/import-envelope/retain"
                (raw
                    "application/vnd.claimcore.recovery+json"
                    TransportLimits.RecoveryArtifactBytes
                    sourceDigestHeader)
                None
        ]

    let private authorityManagement () =
        [
            postJson
                (WebManagementSchemas.response "authority.register")
                "authority.register"
                "/api/v3/authority/register"
                WebManagementSchemas.register
            postJson
                (WebManagementSchemas.response "authority.setGrant")
                "authority.setGrant"
                "/api/v3/authority/grants/set"
                WebManagementSchemas.setGrant
            postJson
                (WebManagementSchemas.response "authority.setEnabled")
                "authority.setEnabled"
                "/api/v3/authority/actors/enable"
                WebManagementSchemas.setEnabled
            postJson
                (WebManagementSchemas.response "authority.observe")
                "authority.observe"
                "/api/v3/authority/observe"
                WebManagementSchemas.observe
        ]

    let private authorityApprovals () =
        [
            postJson
                (WebSignerApprovalSchemas.response)
                "authority.approveCopySigner"
                "/api/v3/authority/copy-signers/approve"
                WebSignerApprovalSchemas.request
            postJson
                (WebCopyDeletionApprovalSchemas.response)
                "authority.approveCopyDeletion"
                "/api/v3/authority/copies/deletion/approve"
                WebCopyDeletionApprovalSchemas.request
            postJson
                (WebCopyAdoptionApprovalSchemas.response)
                "authority.approveCopyAdoption"
                "/api/v3/authority/copies/adoption/approve"
                WebCopyAdoptionApprovalSchemas.request
            postJson
                (WebWriterHandoffApprovalSchemas.response)
                "authority.approveWriterHandoff"
                "/api/v3/authority/writer-handoffs/approve"
                WebWriterHandoffApprovalSchemas.request
            postJson
                (WebRealDataActivationSchemas.reviewResponse)
                "authority.reviewRealDataActivation"
                "/api/v3/authority/real-data-activation/review"
                WebRealDataActivationSchemas.reviewRequest
            postJson
                (WebRealDataActivationSchemas.approvalResponse)
                "authority.approveRealDataActivation"
                "/api/v3/authority/real-data-activation/approve"
                WebRealDataActivationSchemas.approvalRequest
        ]

    let private authority () =
        authorityManagement () @ authorityApprovals ()

    let private lifecycle () =
        [
            postJson
                (WebLifecycleSchemas.reviewResponse)
                "lifecycle.review"
                "/api/v3/lifecycle/review"
                WebLifecycleSchemas.reviewInput
            postJson
                (WebLifecycleSchemas.writeResponse "lifecycle.apply")
                "lifecycle.apply"
                "/api/v3/lifecycle/apply"
                WebLifecycleSchemas.applyInput
            postJson
                (WebLifecycleSchemas.writeResponse "lifecycle.approve")
                "lifecycle.approve"
                "/api/v3/lifecycle/approve"
                WebLifecycleSchemas.approveInput
        ]

    let private tombstones () =
        [
            postJson
                (WebTombstoneSchemas.reviewResponse)
                "tombstone.review"
                "/api/v3/tombstones/review"
                WebTombstoneSchemas.reviewInput
            postJson
                (WebTombstoneSchemas.writeResponse "tombstone.approvePrune")
                "tombstone.approvePrune"
                "/api/v3/tombstones/prune/approve"
                WebTombstoneSchemas.approveInput
            postJson
                (WebTombstoneSchemas.writeResponse "tombstone.approveTerminal")
                "tombstone.approveTerminal"
                "/api/v3/tombstones/terminal/approve"
                WebTerminalApprovalSchemas.request
            postJson
                (WebTombstoneSchemas.writeResponse "tombstone.changeHold")
                "tombstone.changeHold"
                "/api/v3/tombstones/holds/change"
                WebTombstoneSchemas.holdInput
        ]

    let all (semantic: SemanticCoreContract) =
        sessions ()
        @ cases semantic
        @ recoveryJson semantic
        @ recoveryRaw ()
        @ authority ()
        @ lifecycle ()
        @ tombstones ()
