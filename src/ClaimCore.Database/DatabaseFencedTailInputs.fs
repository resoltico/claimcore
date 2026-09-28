namespace ClaimCore.Database

open System.Security.Cryptography
open ClaimCore.HostSecurity

[<NoEquality; NoComparison>]
type internal LoadedFencedTail =
    {
        ReportFiles: RestoreReportFiles
        Evidence: SignedFencedTailEvidence
    }

module internal DatabaseFencedTailInputs =
    let private read maximum exact path =
        match PrivateFileService.readBinary maximum path with
        | Ok bytes when bytes.Length > 0 && (not exact || bytes.Length = maximum) -> bytes
        | _ -> invalidOp "Owner-private signed fenced-tail file is unavailable."

    let load (paths: FencedTailPaths) =
        let report =
            DatabaseRestoreReportInputs.load paths.Report paths.ReportSignature paths.EvidenceIndex
            |> Result.defaultWith (fun _ -> invalidOp "Owner-private pre-W1 report is unavailable.")

        try
            let fence = read 32768 false paths.Fence

            try
                let fenceSignature = read 64 true paths.FenceSignature

                try
                    let supplement = read 8388608 false paths.Supplement

                    try
                        let supplementSignature = read 64 true paths.SupplementSignature

                        {
                            ReportFiles = report
                            Evidence =
                                {
                                    Report = report.Report
                                    ReportSignature = report.Signature
                                    Fence = fence
                                    FenceSignature = fenceSignature
                                    Supplement = supplement
                                    SupplementSignature = supplementSignature
                                }
                        }
                    with _ ->
                        CryptographicOperations.ZeroMemory(supplement)
                        reraise ()
                with _ ->
                    CryptographicOperations.ZeroMemory(fenceSignature)
                    reraise ()
            with _ ->
                CryptographicOperations.ZeroMemory(fence)
                reraise ()
        with _ ->
            DatabaseRestoreReportInputs.dispose report
            reraise ()

    let dispose (loaded: LoadedFencedTail) =
        DatabaseRestoreReportInputs.dispose loaded.ReportFiles
        CryptographicOperations.ZeroMemory(loaded.Evidence.Fence)
        CryptographicOperations.ZeroMemory(loaded.Evidence.FenceSignature)
        CryptographicOperations.ZeroMemory(loaded.Evidence.Supplement)
        CryptographicOperations.ZeroMemory(loaded.Evidence.SupplementSignature)
