module internal ClaimCore.WebTests.TestServerRouteInputs

open Expecto
open ClaimCore.WebTests.TestServerFixture


let private operation = "40000000-0000-4000-8000-000000000001"

let private draft =
    """{"operationId":"40000000-0000-4000-8000-000000000001","caseReference":"WEB-V2-001","expectedRevision":"0","command":{"kind":"OPEN","values":{"incidentDate":"2026-09-01","incidentNotificationDate":"2026-09-02","incidentCountry":"Latvia","claimantName":"Synthetic claimant","insurerName":"Synthetic insurer","claimedAmount":"12.34","claimedCurrency":"EUR"}}}"""

let private adoptionInput =
    $"""{{
      "approvalId":"{operation}",
      "adoptionEventId":"50000000-0000-4000-8000-000000000001",
      "copyId":"60000000-0000-4000-8000-000000000001",
      "caseId":"70000000-0000-4000-8000-000000000001",
      "origin":{{"kind":"PRODUCT_EXPORT","exportId":"b0000000-0000-4000-8000-000000000001","receiptSequence":"41","receiptHash":"{digest}"}},
      "ciphertextSha256":"{digest}","ciphertextBytes":"512",
      "capturedAt":"2026-09-20T00:00:00.0000000+00:00",
      "retainUntil":"2026-10-20T00:00:00.0000000+00:00",
      "locationCommitment":"{digest}","custodianCommitment":"{digest}",
      "custodianSigningKeyId":"80000000-0000-4000-8000-000000000001",
      "registrySigningKeyId":"90000000-0000-4000-8000-000000000001",
      "inspectorSigningKeyId":"a0000000-0000-4000-8000-000000000001",
      "custodianCanonicalSha256":"{digest}",
      "registryCanonicalSha256":"{digest}",
      "inspectionReportSha256":"{digest}",
      "expiresAt":"2026-10-01T00:00:00.0000000+00:00"
    }}"""

let private managementInput identifier =
    match identifier with
    | "authority.register" ->
        $"""{{"eventId":"{operation}","principal":{{"kind":"HUMAN","issuer":"https://issuer.example.test/realms/synthetic","subject":"synthetic-steward"}}}}"""
    | "authority.setGrant" ->
        $"""{{"eventId":"{operation}","principal":{{"kind":"HUMAN","issuer":"https://issuer.example.test/realms/synthetic","subject":"synthetic-steward"}},"role":"DATA_STEWARD","scope":{{"kind":"INSTALLATION"}},"active":true}}"""
    | "authority.setEnabled" ->
        $"""{{"eventId":"{operation}","principal":{{"kind":"HUMAN","issuer":"https://issuer.example.test/realms/synthetic","subject":"synthetic-steward"}},"enabled":false}}"""
    | "authority.observe" -> $"""{{"eventId":"{operation}"}}"""
    | "authority.approveCopySigner" ->
        $"""{{"approvalId":"{operation}","signingKeyId":"50000000-0000-4000-8000-000000000001","action":"REGISTER","purpose":"COPY_ATTESTOR","publicKeySha256":"{digest}","approvalRole":"OWNER","holderApprovalId":"50000000-0000-4000-8000-000000000002","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""
    | "authority.approveCopyDeletion" ->
        $"""{{"approvalId":"{operation}","deletionEventId":"50000000-0000-4000-8000-000000000001","copyId":"60000000-0000-4000-8000-000000000001","verifierSigningKeyId":"70000000-0000-4000-8000-000000000001","expectedCopyRevision":"2","locationCommitment":"{digest}","inspectionReportSha256":"{digest}","witnessCutoffSequence":"41","witnessCutoffHash":"{digest}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""
    | "authority.approveCopyAdoption" -> adoptionInput
    | "authority.approveWriterHandoff" ->
        $"""{{"approvalId":"{operation}","handoffId":"50000000-0000-4000-8000-000000000001","oldGeneration":"2","expectedWitnessSequence":"41","expectedWitnessHash":"{digest}","newCapabilitySha256":"{digest}","checkpointSigningKeyId":"60000000-0000-4000-8000-000000000001","fenceReportSha256":"{digest}","inventorySha256":"{digest}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""
    | "authority.reviewRealDataActivation" -> $"""{{"planId":"{operation}"}}"""
    | "authority.approveRealDataActivation" ->
        $"""{{"approvalId":"{operation}","planId":"50000000-0000-4000-8000-000000000001","activationId":"60000000-0000-4000-8000-000000000001","installationId":"70000000-0000-4000-8000-000000000001","lineageId":"80000000-0000-4000-8000-000000000001","epoch":"1","writerGeneration":"1","activationPlanSha256":"{digest}","policySha256":"{digest}","reviewWitnessSequence":"41","reviewWitnessHash":"{digest}","expectedWitnessSequence":"41","expectedWitnessHash":"{digest}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""
    | _ -> failtest "Unknown management endpoint."

let private caseInput identifier =
    match identifier with
    | "case.get" -> """{"caseReference":"WEB-V2-001"}"""
    | "case.list" -> """{"limit":10}"""
    | "recovery.list" -> """{"view":"PENDING","limit":10}"""
    | "case.history" -> """{"caseReference":"WEB-V2-001","limit":10,"detail":"FULL"}"""
    | "operation.observe" -> $"""{{"operationId":"{operation}"}}"""
    | "recovery.inspect" -> $"""{{"operationId":"{operation}","attemptLimit":10}}"""
    | "command.prepare" -> draft
    | "command.execute" -> draft
    | "recovery.resolve" -> $"""{{"operationId":"{operation}","requestSha256":"{digest}"}}"""
    | "recovery.dismiss" ->
        $"""{{"operationId":"{operation}","requestSha256":"{digest}","confirmed":true}}"""
    | "recovery.export" -> $"""{{"operationId":"{operation}","requestSha256":"{digest}"}}"""
    | _ -> failtest "Every generated JSON endpoint needs a synthetic valid request."

let input (identifier: string) =
    if identifier.StartsWith("authority.", System.StringComparison.Ordinal) then
        managementInput identifier
    elif identifier.StartsWith("lifecycle.", System.StringComparison.Ordinal) then
        TestServerLifecycleInputs.input identifier
    elif identifier.StartsWith("tombstone.", System.StringComparison.Ordinal) then
        TestServerTombstoneRouteTests.input identifier
    else
        caseInput identifier
