namespace ClaimCore.Contracts

/// One exact identifier-to-schema inventory for the generated and hosted Web-v3 API.
module internal WebResponseCatalog =
    let all () =
        [
            "session", WebResponseSchemas.session "session"
            "session.logout", WebResponseSchemas.session "session.logout"
            "definition", WebResponseSchemas.definition
            "case.get", WebResponseSchemas.caseGet
            "case.list", WebResponseSchemas.caseList
            "case.history", WebResponseSchemas.history
            "operation.observe", WebResponseSchemas.observe
            "command.prepare", WebResponseSchemas.prepare
            "command.execute", WebResponseSchemas.submission
            "recovery.list", WebResponseSchemas.recoveryList
            "recovery.inspect", WebResponseSchemas.recoveryInspect
            "recovery.resolve", WebResponseSchemas.resolve "recovery.resolve"
            "recovery.dismiss", WebResponseSchemas.dismiss
            "recovery.export", WebResponseSchemas.export
            "recovery.importEnvelopePreview",
            WebResponseSchemas.importPreview "recovery.importEnvelopePreview"
            "recovery.importEnvelopeRetain",
            WebResponseSchemas.importRetain "recovery.importEnvelopeRetain"
            "authority.register", WebManagementSchemas.response "authority.register"
            "authority.setGrant", WebManagementSchemas.response "authority.setGrant"
            "authority.setEnabled", WebManagementSchemas.response "authority.setEnabled"
            "authority.observe", WebManagementSchemas.response "authority.observe"
            "authority.approveCopySigner", WebSignerApprovalSchemas.response
            "authority.approveCopyDeletion", WebCopyDeletionApprovalSchemas.response
            "authority.approveCopyAdoption", WebCopyAdoptionApprovalSchemas.response
            "authority.approveWriterHandoff", WebWriterHandoffApprovalSchemas.response
            "authority.reviewRealDataActivation", WebRealDataActivationSchemas.reviewResponse
            "authority.approveRealDataActivation", WebRealDataActivationSchemas.approvalResponse
            "lifecycle.review", WebLifecycleSchemas.reviewResponse
            "lifecycle.apply", WebLifecycleSchemas.writeResponse "lifecycle.apply"
            "lifecycle.approve", WebLifecycleSchemas.writeResponse "lifecycle.approve"
            "tombstone.review", WebTombstoneSchemas.reviewResponse
            "tombstone.approvePrune", WebTombstoneSchemas.writeResponse "tombstone.approvePrune"
            "tombstone.approveTerminal",
            WebTombstoneSchemas.writeResponse "tombstone.approveTerminal"
            "tombstone.changeHold", WebTombstoneSchemas.writeResponse "tombstone.changeHold"
        ]
