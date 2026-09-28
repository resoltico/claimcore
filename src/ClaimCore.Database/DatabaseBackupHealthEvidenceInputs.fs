namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open ClaimCore.HostSecurity

[<NoEquality; NoComparison>]
type internal LoadedBackupHealthEvidence =
    {
        PolicyBytes: byte array
        SourceBytes: byte array
        ArchiveSignature: byte array
        CheckpointSignature: byte array
        RestoreSignature: byte array
        CertificateBytes: byte array
        CertificateSignature: byte array
    }

module internal DatabaseBackupHealthEvidenceInputs =
    let private read maximum exact path =
        match PrivateFileService.readBinary maximum path with
        | Ok bytes when bytes.Length > 0 && (not exact || bytes.Length = maximum) -> bytes
        | _ -> invalidOp "Owner-private backup health evidence is unavailable."

    let private sourcePrefix (path: string) =
        let suffix = ".source.json"

        if not (path.EndsWith(suffix, StringComparison.Ordinal)) then
            invalidOp "Independent backup health source filename is invalid."

        path.Substring(0, path.Length - suffix.Length)

    let load (policyPath: string) (sourcePath: string) =
        let directory =
            Path.GetDirectoryName(sourcePath)
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                invalidOp "Independent backup health directory is unavailable.")

        match PrivateFileService.requirePrivateDirectory directory with
        | Ok() -> ()
        | Error _ -> invalidOp "Independent backup health directory is unsafe."

        let prefix = sourcePrefix sourcePath
        let mutable retained: byte array list = []

        let keep maximum exact path =
            let bytes = read maximum exact path
            retained <- bytes :: retained
            bytes

        try
            {
                PolicyBytes = keep 65536 false policyPath
                SourceBytes = keep 131072 false sourcePath
                ArchiveSignature = keep 64 true (prefix + ".archive.sig")
                CheckpointSignature = keep 64 true (prefix + ".checkpoint.sig")
                RestoreSignature = keep 64 true (prefix + ".test-restore.sig")
                CertificateBytes = keep 65536 false (prefix + ".certificate.json")
                CertificateSignature = keep 64 true (prefix + ".certificate.sig")
            }
        with _ ->
            retained |> List.iter CryptographicOperations.ZeroMemory
            reraise ()

    let dispose (loaded: LoadedBackupHealthEvidence) =
        [
            loaded.PolicyBytes
            loaded.SourceBytes
            loaded.ArchiveSignature
            loaded.CheckpointSignature
            loaded.RestoreSignature
            loaded.CertificateBytes
            loaded.CertificateSignature
        ]
        |> List.iter CryptographicOperations.ZeroMemory
