namespace ClaimCore.Database

open System
open System.Security.Cryptography
open ClaimCore.HostSecurity

[<NoEquality; NoComparison>]
type internal RestoreReportFiles =
    {
        Report: byte array
        Signature: byte array
        EvidenceIndex: byte array
        ReportSha256: string
        EvidenceIndexSha256: string
    }

module internal DatabaseRestoreReportInputs =
    let private read maximum path =
        match PrivateFileService.readBinary maximum path with
        | Ok bytes when bytes.Length > 0 -> Some bytes
        | _ -> None

    let load reportPath signaturePath indexPath =
        match read 131072 reportPath with
        | None -> Error DatabaseInputProblem.RestoreEvidenceFileRefused
        | Some report ->
            try
                match read 64 signaturePath with
                | None -> Error DatabaseInputProblem.RestoreEvidenceFileRefused
                | Some signature ->
                    try
                        if signature.Length <> 64 then
                            Error DatabaseInputProblem.RestoreEvidenceFileRefused
                        else
                            match read 131072 indexPath with
                            | None -> Error DatabaseInputProblem.RestoreEvidenceFileRefused
                            | Some evidenceIndex ->
                                try
                                    Ok
                                        {
                                            Report = Array.copy report
                                            Signature = Array.copy signature
                                            EvidenceIndex = Array.copy evidenceIndex
                                            ReportSha256 =
                                                report
                                                |> SHA256.HashData
                                                |> Convert.ToHexStringLower
                                            EvidenceIndexSha256 =
                                                evidenceIndex
                                                |> SHA256.HashData
                                                |> Convert.ToHexStringLower
                                        }
                                finally
                                    CryptographicOperations.ZeroMemory(evidenceIndex)
                    finally
                        CryptographicOperations.ZeroMemory(signature)
            finally
                CryptographicOperations.ZeroMemory(report)

    let dispose (files: RestoreReportFiles) =
        CryptographicOperations.ZeroMemory(files.Report)
        CryptographicOperations.ZeroMemory(files.Signature)
        CryptographicOperations.ZeroMemory(files.EvidenceIndex)
