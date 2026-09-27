namespace ClaimCore.Database

open System
open System.IO
open ClaimCore.HostSecurity

[<NoEquality; NoComparison>]
type internal RestoreProductionPaths =
    {
        EvidenceIndexFile: string
        ReportFile: string
        ReportSignatureFile: string
    }

[<RequireQualifiedAccess>]
type internal RestoreProductionDelivery =
    | Published
    | Unconfirmed

/// The detached signature is written last. A crash before it leaves no admissible report;
/// existing files are never replaced and failed partial evidence is retained for owner review.
module internal DatabaseRestoreProduceOutput =
    let private require path suffix =
        if
            String.IsNullOrWhiteSpace path
            || not (Path.IsPathFullyQualified path)
            || not (path.EndsWith(suffix, StringComparison.Ordinal))
        then
            invalidOp "Restore report output path is invalid."

    let private write maximum path bytes =
        match PrivateFileService.writeNew maximum path bytes with
        | Ok() -> ()
        | Error _ -> invalidOp "Owner-private restore report output was refused."

    let publish (paths: RestoreProductionPaths) (value: SignedRestoreProduction) =
        require paths.EvidenceIndexFile ".json"
        require paths.ReportFile ".json"
        require paths.ReportSignatureFile ".sig"

        if
            paths.EvidenceIndexFile = paths.ReportFile
            || paths.ReportFile = paths.ReportSignatureFile
            || paths.EvidenceIndexFile = paths.ReportSignatureFile
            || value.ReportSignature.Length <> 64
            || File.Exists(paths.EvidenceIndexFile)
            || File.Exists(paths.ReportFile)
            || File.Exists(paths.ReportSignatureFile)
        then
            invalidOp "Restore report output identities are unavailable or already exist."

        try
            write 131072 paths.EvidenceIndexFile value.Evidence.EvidenceIndex
            write 131072 paths.ReportFile value.Evidence.Report
            write 64 paths.ReportSignatureFile value.ReportSignature
            RestoreProductionDelivery.Published
        with _ ->
            // A failed confirmation after signature creation cannot prove nonpublication.
            RestoreProductionDelivery.Unconfirmed
