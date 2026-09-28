module ClaimCore.IntegrationTests.DatabaseTerminalCopyAbsenceProcessTests

open System
open System.IO
open System.Text
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.CaseTombstoneTerminalApprovalFixture
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence
open ClaimCore.IntegrationTests.DatabaseTerminalCopyAbsenceTests
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.Fixtures

let private inputs directory (fixture: PruneFixture) (signed: SignedAbsence) =
    let capability =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic writer capability is absent")

    files directory fixture.Owner fixture.Writer fixture.Witness
    @ [
        "CLAIMCORE_WRITER_CAPABILITY_FILE", capability
        "CLAIMCORE_COPY_LOCATION_REGISTRY_FILE", signed.RegistryPath
        "CLAIMCORE_COPY_LOCATION_INSPECTION_FILE", signed.InspectionPath
        "CLAIMCORE_COPY_COMMITMENT_KEY_FILE", signed.KeyPath
    ]

let private tamperedInspection directory (signed: SignedAbsence) configured =
    let original = Encoding.ASCII.GetString(File.ReadAllBytes(signed.InspectionPath))
    let marker = "\"signatureBase64\":\""
    let position = original.IndexOf(marker, StringComparison.Ordinal)
    Expect.isTrue (position >= 0) "Signed inspection envelope has signature"
    let altered = original.ToCharArray()
    let signatureIndex = position + marker.Length
    altered[signatureIndex] <- if altered[signatureIndex] = 'A' then 'B' else 'A'

    let path =
        privateBytes
            directory
            "tampered-terminal-inspection.json"
            (Encoding.ASCII.GetBytes(altered))

    configured
    |> List.map (fun (name, value) ->
        if name = "CLAIMCORE_COPY_LOCATION_INSPECTION_FILE" then
            name, path
        else
            name, value)

let private refused (signed: SignedAbsence) configured =
    let rejected, refusal =
        runCommand "certify-managed-payload-absence" [ signed.ProposalPath ] configured

    use refusal = refusal
    Expect.isGreaterThan rejected 0 "Changed independent signature is refused"

    Expect.equal
        (refusal.RootElement.GetProperty("operationOutcome").GetString())
        "NOT_COMMITTED"
        "Signature refusal writes no terminal event"

let private missingSuppression directory (signed: SignedAbsence) configured =
    let missing =
        configured
        |> List.map (fun (name, value) ->
            if name = "CLAIMCORE_SUPPRESSION_KEY_FILE" then
                name, Path.Combine(directory, "absent-suppression.key")
            else
                name, value)

    let code, response =
        runCommand "certify-managed-payload-absence" [ signed.ProposalPath ] missing

    use response = response
    Expect.isGreaterThan code 0 "Missing suppression key is refused before mutation"

    Expect.equal
        (response.RootElement.GetProperty("operationOutcome").GetString())
        "NOT_STARTED"
        "Missing private key is a definite input refusal"

let private completed (fixture: PruneFixture) (signed: SignedAbsence) configured =
    let code, response =
        runCommand "certify-managed-payload-absence" [ signed.ProposalPath ] configured

    use response = response
    Expect.equal code 0 "Owner process returns definite completion"
    let safe = response.RootElement.GetRawText()

    Expect.isFalse
        (safe.Contains(signed.ProposalPath, StringComparison.Ordinal))
        "Private proposal path stays out of owner output"

    for path in [ signed.RegistryPath; signed.InspectionPath; signed.KeyPath ] do
        Expect.isFalse
            (safe.Contains(path, StringComparison.Ordinal))
            "Private signed copy evidence stays out of owner output"

    Expect.equal
        (response.RootElement.GetProperty("operationOutcome").GetString())
        "COMPLETED"
        "Signed owner result is definite"

    Expect.equal
        (phase fixture.Owner fixture.CaseId)
        "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
        "Process route advances only intermediate privacy phase"

let private wrongTerminalAction (fixture: PruneFixture) (signed: SignedAbsence) configured =
    let code, response =
        runCommand "complete-suppression-horizon" [ signed.ProposalPath ] configured

    use response = response
    Expect.isGreaterThan code 0 "An intermediate proposal cannot invoke final erasure"

    Expect.equal
        (response.RootElement.GetProperty("operationOutcome").GetString())
        "NOT_STARTED"
        "The distinct final command refuses the wrong signed proposal kind"

    Expect.equal
        (phase fixture.Owner fixture.CaseId)
        "ERASURE_PENDING"
        "No final mutation follows a wrong-kind proposal"

let private prematureFinal directory (fixture: PruneFixture) (signed: SignedAbsence) configured =
    let copy =
        match signed.Proposal with
        | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence value -> value
        | _ -> failtest "Synthetic intermediate proposal is unavailable"

    let final =
        TombstoneTerminalProposal.CompleteSuppressionHorizon
            {
                Copy =
                    { copy with
                        EventId = Guid.NewGuid()
                        ExpectedWriterGeneration = 2L
                    }
                RecoveryFenceDigest = String.replicate 64 "a"
                OldWriterGeneration = 1L
                NewWriterGeneration = 2L
            }

    let path =
        privateBytes
            directory
            "premature-terminal-final.json"
            (CaseTombstoneTerminalCandidate.proposal final)

    let code, response = runCommand "complete-suppression-horizon" [ path ] configured
    use response = response
    Expect.isGreaterThan code 0 "A final request before W2 and the intermediate phase is refused"

    Expect.equal
        (response.RootElement.GetProperty("operationOutcome").GetString())
        "NOT_COMMITTED"
        "A well-formed final proposal cannot create authority prematurely"

    Expect.equal
        (phase fixture.Owner fixture.CaseId)
        "ERASURE_PENDING"
        "Premature final evidence changes no privacy phase"

let private processPositive =
    testCase
        "[CC-ERASE-001] owner process certifies signed zero-copy absence without private output"
        (fun _ ->
            withPruned (fun fixture _ runtime proposer first second _ ->
                let signed =
                    signedDocuments fixture.Owner fixture.Witness runtime proposer fixture []

                approved runtime first second signed.Proposal

                let directory =
                    Path.GetDirectoryName(signed.ProposalPath)
                    |> Option.ofObj
                    |> Option.defaultWith (fun () ->
                        failtest "Synthetic private directory is absent")

                let configured = inputs directory fixture signed
                missingSuppression directory signed configured
                refused signed (tamperedInspection directory signed configured)
                wrongTerminalAction fixture signed configured
                prematureFinal directory fixture signed configured
                completed fixture signed configured))

let tests =
    testList "terminal signed copy absence owner process" [ processPositive ]
