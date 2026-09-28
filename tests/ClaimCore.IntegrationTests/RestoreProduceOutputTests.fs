module ClaimCore.IntegrationTests.RestoreProduceOutputTests

open System
open System.IO
open System.Security.Cryptography
open Expecto
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.IntegrationTests.RestoreProduceCanonicalTests

let private withPrivateDirectory action =
    let temporary = Path.GetTempPath()

    let physical =
        if
            OperatingSystem.IsMacOS()
            && (temporary.StartsWith("/var/", StringComparison.Ordinal)
                || temporary.StartsWith("/tmp/", StringComparison.Ordinal))
        then
            "/private" + temporary
        else
            temporary

    let directory =
        Path.Combine(physical, "claimcore-restore-output-" + Guid.NewGuid().ToString("N"))

    let mode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    Directory.CreateDirectory(directory, mode) |> ignore

    try
        action directory
    finally
        Directory.Delete(directory, true)

let private outputIsCreateOnly =
    testCase
        "[CC-BACKUP-001] restore report output is private create-only with signature last"
        (fun _ ->
            if not (OperatingSystem.IsWindows()) then
                withPrivateDirectory (fun directory ->
                    let claims, index, publication = specimen ()

                    let evidence =
                        DatabaseRestoreProduceCanonical.produce claims index publication

                    let produced =
                        {
                            Evidence = evidence
                            ReportSignature = Array.zeroCreate 64
                        }

                    let paths =
                        {
                            EvidenceIndexFile = Path.Combine(directory, "index.json")
                            ReportFile = Path.Combine(directory, "report.json")
                            ReportSignatureFile = Path.Combine(directory, "report.sig")
                        }

                    Expect.equal
                        (DatabaseRestoreProduceOutput.publish paths produced)
                        RestoreProductionDelivery.Published
                        "All three owner-private files were published"

                    Expect.isTrue
                        (File.Exists paths.ReportSignatureFile)
                        "Signature is emitted last"

                    Expect.throws
                        (fun () -> DatabaseRestoreProduceOutput.publish paths produced |> ignore)
                        "Existing restore evidence cannot be overwritten"

                    Expect.equal
                        (File.ReadAllBytes paths.ReportFile)
                        evidence.Report
                        "A retry did not change the signed report bytes"))

let private syntheticCannotPromote =
    testCase "[CC-BACKUP-001] synthetic report cannot cross production recheck" (fun _ ->
        let claims, index, publication = specimen ()

        let evidence = DatabaseRestoreProduceCanonical.produce claims index publication

        let files: RestoreReportFiles =
            {
                Report = evidence.Report
                Signature = Array.zeroCreate 64
                EvidenceIndex = evidence.EvidenceIndex
                ReportSha256 = evidence.ReportSha256
                EvidenceIndexSha256 = evidence.EvidenceIndexSha256
            }

        let result =
            DatabaseRestoreReportRecheck.evaluate
                (Some publication)
                ""
                ""
                ""
                Unchecked.defaultof<_>
                Unchecked.defaultof<_>
                files
                (String('5', 64))
                publication.VerifierBinarySha256
                claims.CheckedAt

        match result with
        | Error RestoreRecheckFailure.ReportInvalid -> ()
        | _ -> failtest "Synthetic-only evidence crossed production recheck")

let private preexistingSignatureRefused =
    testCase
        "[CC-BACKUP-001] preexisting restore signature prevents partial report publication"
        (fun _ ->
            if not (OperatingSystem.IsWindows()) then
                withPrivateDirectory (fun directory ->
                    let claims, index, publication = specimen ()

                    let evidence =
                        DatabaseRestoreProduceCanonical.produce claims index publication

                    let signature = Path.Combine(directory, "report.sig")

                    match PrivateFileService.writeNew 64 signature (Array.zeroCreate 64) with
                    | Ok() -> ()
                    | Error _ -> failtest "Synthetic signature collision setup failed"

                    let paths =
                        {
                            EvidenceIndexFile = Path.Combine(directory, "index.json")
                            ReportFile = Path.Combine(directory, "report.json")
                            ReportSignatureFile = signature
                        }

                    let produced =
                        {
                            Evidence = evidence
                            ReportSignature = Array.zeroCreate 64
                        }

                    Expect.throws
                        (fun () -> DatabaseRestoreProduceOutput.publish paths produced |> ignore)
                        "A preexisting signature refuses before other files are written"

                    Expect.isFalse
                        (File.Exists paths.ReportFile)
                        "No partial report was published"

                    Expect.isFalse
                        (File.Exists paths.EvidenceIndexFile)
                        "No partial index was published"))

let private lostSignatureConfirmation =
    testCase
        "[CC-BACKUP-001] report delivery loss after index and report stays unconfirmed"
        (fun _ ->
            if not (OperatingSystem.IsWindows()) then
                withPrivateDirectory (fun directory ->
                    let claims, index, publication = specimen ()

                    let evidence =
                        DatabaseRestoreProduceCanonical.produce claims index publication

                    let paths =
                        {
                            EvidenceIndexFile = Path.Combine(directory, "index.json")
                            ReportFile = Path.Combine(directory, "report.json")
                            ReportSignatureFile =
                                Path.Combine(directory, "absent-parent", "report.sig")
                        }

                    let produced =
                        {
                            Evidence = evidence
                            ReportSignature = Array.zeroCreate 64
                        }

                    Expect.equal
                        (DatabaseRestoreProduceOutput.publish paths produced)
                        RestoreProductionDelivery.Unconfirmed
                        "Signature delivery failure does not claim rollback"

                    Expect.isTrue (File.Exists paths.ReportFile) "Report bytes may be present"

                    Expect.isTrue
                        (File.Exists paths.EvidenceIndexFile)
                        "Index bytes may be present"))

let private checkpointKeyClaim =
    testCase "[CC-BACKUP-001] signed checkpoint custody key digest is exact" (fun _ ->
        let report, index, _ = specimen ()
        let publicKey = RandomNumberGenerator.GetBytes(32)

        let digest =
            SHA256.HashData(ReadOnlySpan<byte>(publicKey)) |> Convert.ToHexStringLower

        let bound =
            { report with
                CustodyPublicKeySha256 = digest
            }

        Expect.isTrue
            (DatabaseRestoreSignedEvidence.checkpointMatches bound index publicKey)
            "Registered checkpoint key satisfies the signed identity"

        Expect.isFalse
            (DatabaseRestoreSignedEvidence.checkpointMatches report index publicKey)
            "A changed signed public-key digest refuses"

        let wrong =
            { bound with
                CustodyKeyId = Guid.NewGuid()
            }

        Expect.isFalse
            (DatabaseRestoreSignedEvidence.checkpointMatches wrong index publicKey)
            "A different custody key identity refuses")

let tests =
    testList
        "restore producer output"
        [
            outputIsCreateOnly
            syntheticCannotPromote
            preexistingSignatureRefused
            lostSignatureConfirmation
            checkpointKeyClaim
        ]
