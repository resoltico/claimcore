namespace ClaimCore.Database

open System
open System.Buffers
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.HostSecurity

module internal DatabaseRestoreReportExecution =
    let private reason =
        function
        | RestoreRecheckFailure.TrustAnchorUnavailable -> "TRUST_ANCHOR_UNAVAILABLE"
        | RestoreRecheckFailure.ReportInvalid -> "REPORT_INVALID"
        | RestoreRecheckFailure.EvidenceIndexInvalid -> "EVIDENCE_INDEX_INVALID"
        | RestoreRecheckFailure.EvidenceMismatch -> "EVIDENCE_RECHECK_FAILED"

    let private binarySha () =
        let location = typeof<DatabaseCommand>.Assembly.Location

        if String.IsNullOrWhiteSpace location then
            invalidOp "Published verifier assembly location is unavailable."

        let file = FileInfo(location)

        if not file.Exists || file.Length < 1L || file.Length > 100000000L then
            invalidOp "Published verifier assembly bounds are invalid."

        use stream = file.OpenRead()
        SHA256.HashData(stream) |> Convert.ToHexStringLower

    let private inputs () =
        DatabaseWitnessInputs.witnessAuditConnection ()
        |> Result.bind (fun witnessAudit ->
            DatabaseWitnessInputs.witnessOwnerConnection ()
            |> Result.bind (fun witnessOwner ->
                DatabaseWitnessInputs.keyRing ()
                |> Result.map (fun custody -> witnessAudit, witnessOwner, custody)))

    let private evaluate owner reportPath signaturePath indexPath nonce =
        match DatabaseRestorePublication.current () with
        | None -> Error RestoreRecheckFailure.TrustAnchorUnavailable
        | Some publication ->
            match DatabaseRestoreReportInputs.load reportPath signaturePath indexPath with
            | Error _ -> Error RestoreRecheckFailure.EvidenceMismatch
            | Ok files ->
                try
                    match inputs () with
                    | Error _ -> Error RestoreRecheckFailure.EvidenceMismatch
                    | Ok(witnessAudit, witnessOwner, custody) ->
                        use custody = custody

                        match
                            Environment.GetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE")
                        with
                        | null
                        | "" -> Error RestoreRecheckFailure.EvidenceMismatch
                        | path ->
                            try
                                use suppression = SuppressionKeyFile.Load(path)

                                DatabaseRestoreReportRecheck.evaluate
                                    (Some publication)
                                    owner
                                    witnessAudit
                                    witnessOwner
                                    custody
                                    suppression
                                    files
                                    nonce
                                    (binarySha ())
                                    DateTimeOffset.UtcNow
                            with _ ->
                                Error RestoreRecheckFailure.EvidenceMismatch
                finally
                    DatabaseRestoreReportInputs.dispose files

    let private encode
        (nonce: string)
        (outcome: Result<RestoreRecheckResult, RestoreRecheckFailure>)
        =
        let output = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(output)
        writer.WriteStartObject()
        writer.WriteString("format", "claimcore-restore-recheck-1")
        writer.WriteString("nonce", nonce)

        match outcome with
        | Ok proof ->
            writer.WriteString("status", "EVIDENCE_RECHECKED")
            writer.WriteString("reportSha256", proof.ReportSha256)
            writer.WriteString("evidenceIndexSha256", proof.EvidenceIndexSha256)
            writer.WriteNumber("witnessCutoff", proof.WitnessCutoff)
            writer.WriteString("witnessCutoffHash", proof.WitnessCutoffHash)
        | Error refusal ->
            writer.WriteString("status", "REFUSED")
            writer.WriteString("reason", reason refusal)

        writer.WriteBoolean("realDataReady", false)
        writer.WriteEndObject()
        writer.Flush()
        let bytes = Array.zeroCreate<byte>(output.WrittenCount + 1)
        output.WrittenSpan.CopyTo(bytes.AsSpan())
        bytes[bytes.Length - 1] <- byte '\n'
        bytes

    let run owner reportPath signaturePath indexPath nonce (output: Stream) (errors: Stream) =
        let outcome = evaluate owner reportPath signaturePath indexPath nonce
        let target = if Result.isOk outcome then output else errors

        try
            let bytes = encode nonce outcome
            target.Write(bytes, 0, bytes.Length)
            target.Flush()
            if Result.isOk outcome then 0 else 3
        with _ ->
            3
