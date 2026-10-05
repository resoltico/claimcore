namespace ClaimCore.Database

open System.Threading
open System
open System.Globalization
open System.IO
open System.Text.Json
open ClaimCore.Postgres
open ClaimCore.HostSecurity

module internal DatabaseRestoreCheckpoint =
    let private names =
        [
            "format"
            "cycleId"
            "installationId"
            "lineageId"
            "epoch"
            "sequence"
            "hash"
            "capturedAt"
        ]

    let private text name root =
        DatabaseRestoreCanonical.text name root
        |> Option.ofObj
        |> Option.defaultValue ""

    let private require (report: RestoreReportClaims) (root: JsonElement) =
        if
            not (DatabaseRestoreCanonical.exactProperties names root)
            || text "format" root <> "claimcore-witness-checkpoint-1"
            || text "installationId" root <> report.InstallationId.ToString("D")
            || text "lineageId" root <> report.LineageId.ToString("D")
        then
            invalidOp "Independent checkpoint identity differs."

        let epoch = DatabaseRestoreCanonical.number "epoch" root
        let sequence = DatabaseRestoreCanonical.number "sequence" root
        let hash = text "hash" root

        if epoch < 1L || sequence < 0L || hash.Length <> 64 then
            invalidOp "Independent checkpoint tip is invalid."

        let captured = text "capturedAt" root
        let mutable parsed = DateTimeOffset.MinValue

        if
            not (
                DateTimeOffset.TryParseExact(
                    captured,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    &parsed
                )
            )
            || parsed > report.CheckedAt
        then
            invalidOp "Independent checkpoint time is invalid."

        epoch, sequence, hash

    let private compareTip (report: RestoreReportClaims) (epoch, sequence, hash) =
        if
            epoch > report.Epoch
            || (epoch = report.Epoch && sequence > report.WitnessCutoff)
            || (epoch = report.Epoch
                && sequence = report.WitnessCutoff
                && hash <> report.WitnessCutoffHash)
        then
            invalidOp "A retained independent checkpoint detects rollback or divergence."

    let private checkpointSignature (path: string) =
        Path.ChangeExtension(path, ".sig")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Checkpoint signature path is invalid.")

    let verify
        (index: RestoreEvidenceIndex)
        (report: RestoreReportClaims)
        (checkpointKey: byte array)
        (witness: WitnessProtocol)
        =
        task {
            match PrivateFileService.requirePrivateDirectory index.CheckpointRoot with
            | Ok() -> ()
            | Error _ -> invalidOp "Independent checkpoint root is not owner-private."

            use current =
                DatabaseRestoreSignedEvidence.verifyFile
                    16384
                    report.CheckpointSha256
                    checkpointKey
                    index.CheckpointFile
                    index.CheckpointSignatureFile

            let currentTip = require report current.RootElement

            if
                currentTip <> (report.Epoch, report.WitnessCutoff, report.WitnessCutoffHash)
                || text "cycleId" current.RootElement <> report.CycleId.ToString("D")
            then
                invalidOp "The signed checkpoint does not bind the restored cutoff."

            let! captured =
                witness.TryReadHashAtSequence(report.BackupCaptureSequence, CancellationToken.None)

            match captured with
            | Some historical when Convert.ToHexStringLower(historical) = report.BackupCaptureHash ->
                ()
            | _ -> invalidOp "The captured backup cutoff is absent from the restored witness chain."

            let retained =
                Directory.EnumerateFiles(index.CheckpointRoot, "*.json")
                |> Seq.truncate 1001
                |> Seq.toList

            if retained.Length = 0 || retained.Length > 1000 then
                invalidOp "Independent checkpoint inventory is absent or unbounded."

            for path in retained do
                use document =
                    DatabaseRestoreSignedEvidence.verifyFileWith
                        16384
                        None
                        checkpointKey
                        path
                        (checkpointSignature path)

                let epoch, sequence, hash = require report document.RootElement
                compareTip report (epoch, sequence, hash)

                let! observed = witness.TryReadHashAtSequence(sequence, CancellationToken.None)

                match observed with
                | Some witnessed when Convert.ToHexStringLower(witnessed) = hash -> ()
                | _ -> invalidOp "Signed checkpoint diverges from witnessed historical chain."
        }
