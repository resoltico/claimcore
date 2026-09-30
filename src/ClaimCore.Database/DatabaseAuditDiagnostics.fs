namespace ClaimCore.Database

open System
open System.Buffers
open System.Globalization
open System.IO
open System.Text.Json
open ClaimCore.Postgres
open ClaimCore.Witness

/// Safe owner audit output contains only opaque installation identifiers, hashes and counts.
module internal DatabaseAuditDiagnostics =
    let private encoded write =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        write writer
        writer.Flush()
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]

    let private count (writer: Utf8JsonWriter) (name: string) (value: int64) =
        writer.WriteString(name, value.ToString(CultureInfo.InvariantCulture))

    let private verified (summary: DataAuditSummary) (tip: Snapshot) =
        encoded (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("kind", "dataAuditResult")
            writer.WriteString("command", "VERIFY_DATA")
            writer.WriteString("scope", "CURRENT_PRIMARY_AND_WITNESS")

            writer.WriteString(
                "status",
                if summary.PendingIntents = 0L then
                    "VERIFIED"
                else
                    "VERIFIED_WITH_PENDING_INTENTS"
            )

            writer.WriteString("installationId", tip.Identity.InstallationId)
            writer.WriteString("lineageId", tip.Identity.LineageId)
            count writer "epoch" tip.Identity.Epoch
            count writer "witnessCutoff" summary.WitnessCutoff
            writer.WriteString("witnessTipHash", Convert.ToHexStringLower tip.TipHash)

            writer.WriteString(
                "verifiedCaseTipsSha256",
                Convert.ToHexStringLower summary.VerifiedCaseTipsSha256
            )

            writer.WritePropertyName("counts")
            writer.WriteStartObject()
            count writer "cases" summary.Cases
            count writer "acceptedOperations" summary.AcceptedOperations
            count writer "lifecycleEvents" summary.LifecycleEvents
            count writer "erasureFences" summary.ErasureFences
            count writer "terminalApprovals" summary.TerminalApprovals
            count writer "terminalEvents" summary.TerminalEvents
            count writer "revocations" summary.Revocations
            count writer "authorityEvents" summary.AuthorityEvents
            count writer "actors" summary.Actors
            count writer "grants" summary.Grants
            count writer "signerApprovals" summary.SignerApprovals
            count writer "signerKeys" summary.SignerKeys
            count writer "signerEvents" summary.SignerEvents
            count writer "ownerManagedCopies" summary.OwnerManagedCopies
            count writer "copyPhysicalVerifications" summary.CopyPhysicalVerifications
            count writer "copyDeletionApprovals" summary.CopyDeletionApprovals
            count writer "writerHandoffApprovals" summary.WriterHandoffApprovals
            count writer "writerHandoffPreparations" summary.WriterHandoffPreparations
            count writer "writerHandoffs" summary.WriterHandoffs
            count writer "writerActivations" summary.WriterActivations
            count writer "writerHandoffAborts" summary.WriterHandoffAborts
            count writer "managedExports" summary.ManagedExports
            count writer "witnessEntries" summary.WitnessEntries
            count writer "pendingIntents" summary.PendingIntents
            writer.WriteEndObject()
            writer.WriteEndObject())

    let private category =
        function
        | VerifyDataFailure.EvidenceDivergence -> "EVIDENCE_DIVERGENCE"
        | VerifyDataFailure.AuditUnavailable -> "AUDIT_UNAVAILABLE"
        | VerifyDataFailure.AuditFault -> "AUDIT_FAULT"
        | VerifyDataFailure.TopologyRefused -> "TOPOLOGY_REFUSED"

    let private quarantined failure =
        encoded (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("kind", "dataAuditResult")
            writer.WriteString("command", "VERIFY_DATA")
            writer.WriteString("status", "QUARANTINED")
            writer.WritePropertyName("diagnostic")
            writer.WriteStartObject()
            writer.WriteString("id", "DB_DATA_AUDIT_FAILED")
            writer.WritePropertyName("parameters")
            writer.WriteStartObject()
            writer.WriteString("category", category failure)
            writer.WriteEndObject()
            writer.WriteEndObject()
            writer.WriteString("recommendedAction", "INSPECT_AND_RECONCILE")
            writer.WriteEndObject())

    let private write (target: Stream) bytes =
        target.Write(bytes, 0, bytes.Length)
        target.Flush()

    let deliver (outcome: VerifyDataOutcome) (output: Stream) (errors: Stream) =
        match outcome with
        | VerifyDataOutcome.InputRefused reason ->
            try
                write errors (DatabaseDiagnostics.inputFailure reason)
            with _ ->
                ()

            3
        | VerifyDataOutcome.AuditFailed failure ->
            try
                write errors (quarantined failure)
            with _ ->
                ()

            3
        | VerifyDataOutcome.Verified(summary, tip) ->
            try
                write output (verified summary tip)
                0
            with _ ->
                try
                    write
                        errors
                        (DatabaseDiagnostics.deliveryFailure
                            DatabaseCommand.VerifyData
                            (AdministrationOutcome.Completed None))
                with _ ->
                    ()

                3
