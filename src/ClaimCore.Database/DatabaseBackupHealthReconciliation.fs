namespace ClaimCore.Database

open System
open System.Buffers
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.HostSecurity
open ClaimCore.Postgres

[<RequireQualifiedAccess>]
type internal BackupHealthPublicationState =
    | Complete
    | CertificateOnly
    | SignatureOnly
    | Missing
    | Unknown

/// Readback never creates, repairs, deletes or blesses a health certificate.
module internal DatabaseBackupHealthReconciliation =
    [<RequireQualifiedAccess>]
    type private FileState =
        | Exact
        | Missing
        | Changed
        | Unreadable

    let private paths (output: string) =
        if
            not (Path.IsPathFullyQualified(output))
            || not (output.EndsWith(".json", StringComparison.Ordinal))
        then
            invalidOp "Historical health output path is invalid."

        let directory =
            Path.GetDirectoryName(output)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Historical health output has no parent.")

        match PrivateFileService.requirePrivateDirectory directory with
        | Ok() -> output, output + ".sig"
        | Error _ -> invalidOp "Historical health output parent is unsafe."

    let private absentOrUnsafe path =
        try
            let file = FileInfo(path)

            if file.Exists || Directory.Exists(path) || not (isNull file.LinkTarget) then
                FileState.Unreadable
            else
                FileState.Missing
        with _ ->
            FileState.Unreadable

    let private fileState maximum path (expected: byte array) =
        let read () =
            match PrivateFileService.readBinary maximum path with
            | Ok bytes ->
                try
                    if
                        CryptographicOperations.FixedTimeEquals(bytes.AsSpan(), expected.AsSpan())
                    then
                        FileState.Exact
                    else
                        FileState.Changed
                finally
                    CryptographicOperations.ZeroMemory(bytes)
            | Error _ -> absentOrUnsafe path

        match read () with
        | FileState.Exact -> read ()
        | state -> state

    let inspect outputPath (certificate: byte array) (signature: byte array) =
        try
            let certificatePath, signaturePath = paths outputPath

            match
                fileState 65536 certificatePath certificate, fileState 64 signaturePath signature
            with
            | FileState.Exact, FileState.Exact -> BackupHealthPublicationState.Complete
            | FileState.Exact, FileState.Missing -> BackupHealthPublicationState.CertificateOnly
            | FileState.Missing, FileState.Exact -> BackupHealthPublicationState.SignatureOnly
            | FileState.Missing, FileState.Missing -> BackupHealthPublicationState.Missing
            | _ -> BackupHealthPublicationState.Unknown
        with _ ->
            BackupHealthPublicationState.Unknown

    let private token =
        function
        | BackupHealthPublicationState.Complete -> "COMPLETE"
        | BackupHealthPublicationState.CertificateOnly -> "PARTIAL_CERTIFICATE"
        | BackupHealthPublicationState.SignatureOnly -> "PARTIAL_SIGNATURE"
        | BackupHealthPublicationState.Missing -> "MISSING"
        | BackupHealthPublicationState.Unknown -> "UNKNOWN"

    let private action =
        function
        | BackupHealthPublicationState.Complete -> "REVERIFY_CURRENT_HEALTH"
        | BackupHealthPublicationState.CertificateOnly ->
            "PRESERVE_PARTIAL_AND_ISSUE_FRESH_AT_NEW_PATH"
        | BackupHealthPublicationState.SignatureOnly
        | BackupHealthPublicationState.Unknown -> "QUARANTINE_AND_INSPECT"
        | BackupHealthPublicationState.Missing -> "ISSUE_FRESH_AT_NEW_PATH"

    let render state (certificateSha: string option) =
        let output = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(output)
        writer.WriteStartObject()
        writer.WriteString("format", "claimcore-backup-health-publication-readback-1")
        writer.WriteString("command", "RECONCILE_BACKUP_HEALTH")
        writer.WriteString("publicationState", token state)

        match certificateSha with
        | Some digest -> writer.WriteString("expectedCertificateSha256", digest)
        | None -> writer.WriteNull("expectedCertificateSha256")

        writer.WriteBoolean("realDataReady", false)
        writer.WriteString("recommendedAction", action state)
        writer.WriteEndObject()
        writer.Flush()
        let bytes = Array.zeroCreate<byte>(output.WrittenCount + 1)
        output.WrittenSpan.CopyTo(bytes.AsSpan())
        bytes[bytes.Length - 1] <- byte '\n'
        bytes

    let private evaluated owner policyPath evidencePath outputPath =
        task {
            match ReviewedDeploymentRoot.current () with
            | None -> return BackupHealthPublicationState.Unknown, None
            | Some profile ->
                try
                    try
                        let loaded = DatabaseBackupHealthEvidenceInputs.load policyPath evidencePath

                        try
                            let! certificateSha =
                                DatabaseBackupHealthHistorical.verify owner profile loaded

                            return
                                inspect
                                    outputPath
                                    loaded.CertificateBytes
                                    loaded.CertificateSignature,
                                Some certificateSha
                        finally
                            DatabaseBackupHealthEvidenceInputs.dispose loaded
                    with _ ->
                        return BackupHealthPublicationState.Unknown, None
                finally
                    CryptographicOperations.ZeroMemory(profile.PublicationRootKey)
        }

    let run owner policyPath evidencePath outputPath (output: Stream) (errors: Stream) =
        let state, certificateSha =
            (evaluated owner policyPath evidencePath outputPath).GetAwaiter().GetResult()

        let target =
            if state = BackupHealthPublicationState.Unknown then
                errors
            else
                output

        try
            let bytes = render state certificateSha
            target.Write(bytes, 0, bytes.Length)
            target.Flush()

            if state = BackupHealthPublicationState.Unknown then
                3
            else
                0
        with _ ->
            3
