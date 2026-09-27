namespace ClaimCore.Database

open System
open System.Globalization
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Reconstructs the exact originally signed W2 candidate for a historical retry.
/// It does not issue fresh host readiness; Postgres must require the existing W2 witness event.
module internal DatabaseFencedTailHistorical =
    let issuedAt (bytes: byte array) =
        match DatabaseRestoreCanonical.parse bytes with
        | None -> invalidOp "Historical final-tail bytes are not canonical."
        | Some document ->
            use document = document

            let raw =
                DatabaseRestoreCanonical.text "checkedAt" document.RootElement
                |> Option.ofObj
                |> Option.defaultWith (fun () ->
                    invalidOp "Historical final-tail issuance is absent.")

            let parsed =
                DateTimeOffset.ParseExact(
                    raw,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                )

            if parsed.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) <> raw then
                invalidOp "Historical final-tail issuance is invalid."

            parsed

    let private parsed (publication: TrustedRestorePublication) (loaded: LoadedFencedTail) =
        let atIssuance = issuedAt loaded.Evidence.Supplement

        let report =
            DatabaseRestoreReportClaims.parse loaded.Evidence.Report atIssuance
            |> Option.defaultWith (fun () ->
                invalidOp "Historical report was not valid at W2 issuance.")

        let index =
            DatabaseRestoreEvidenceIndex.parse loaded.ReportFiles.EvidenceIndex report
            |> Option.defaultWith (fun () -> invalidOp "Historical evidence index is invalid.")

        let fence =
            DatabaseRestoreWriterFenceClaims.parse loaded.Evidence.Fence atIssuance
            |> Option.defaultWith (fun () -> invalidOp "Historical writer fence is invalid.")

        if
            (report.Scope <> "full" && report.Scope <> "synthetic-only")
            || index.PublicationManifestSha256 <> publication.ManifestSha256
            || report.VerifierBinarySha256 <> publication.VerifierBinarySha256
            || report.SignerKeyId <> publication.ReportSignerKeyId
            || index.CheckpointSignerKeyId <> publication.CheckpointSignerKeyId
            || report.InstallationId <> publication.InstallationId
            || report.LineageId <> publication.LineageId
            || report.Epoch <> publication.Epoch
            || report.WitnessCutoff <> publication.WitnessCutoff
            || report.WitnessCutoffHash <> publication.WitnessCutoffHash
        then
            invalidOp "Historical final-tail publication binding diverged."

        atIssuance, report, index, fence

    let verified
        (ownerConnection: string)
        (witness: WitnessProtocol)
        (publication: TrustedRestorePublication)
        (loaded: LoadedFencedTail)
        (probeEvidenceSha: string)
        (archiveRoot: string)
        =
        let atIssuance, report, index, fence = parsed publication loaded

        let builder = OwnerConnection.builder ownerConnection
        use owner = new NpgsqlConnection(builder.ConnectionString)
        owner.Open()
        witness.AdmitReadOnly()
        use transaction = owner.BeginTransaction()

        let proof =
            DatabaseRestoreFencedTailVerification.verifyHistorical
                owner
                transaction
                witness
                report
                index
                fence.IndependentProbeSha256
                probeEvidenceSha
                archiveRoot
                loaded.Evidence
                atIssuance

        transaction.Rollback()
        proof
