namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Postgres

/// The private adapter proves bytes and keyed custody commitments, not merely signed metadata.
module internal DatabaseAdoptCopyLocationProof =
    let private equal (left: byte array) (right: byte array) =
        left.Length = right.Length
        && CryptographicOperations.FixedTimeEquals(left, right)

    let private identities
        (request: CopyAdoptionApprovalRequest)
        (submission: CopyAdoptionSubmission)
        (custody: CopyAdoptionCustody)
        (registry: CopyAdoptionRegistryReceipt)
        (inspection: CopyAdoptionInspectionReceipt)
        =
        custody.AdoptionEventId = submission.AdoptionEventId
        && custody.OwnerApprovalId = request.ApprovalId
        && custody.CopyId = request.CopyId
        && custody.CaseId = request.CaseId
        && registry.AdoptionEventId = submission.AdoptionEventId
        && registry.CopyId = request.CopyId
        && registry.CaseId = request.CaseId
        && inspection.AdoptionEventId = submission.AdoptionEventId
        && inspection.CopyId = request.CopyId
        && inspection.CaseId = request.CaseId

    let private commitments
        (request: CopyAdoptionApprovalRequest)
        (custody: CopyAdoptionCustody)
        (registry: CopyAdoptionRegistryReceipt)
        (inspection: CopyAdoptionInspectionReceipt)
        =
        equal custody.LocationCommitment request.LocationCommitment
        && equal custody.CustodianCommitment request.CustodianCommitment
        && equal registry.LocationCommitment request.LocationCommitment
        && equal registry.CustodianCommitment request.CustodianCommitment
        && equal inspection.LocationCommitment request.LocationCommitment

    let private bytesAndWindow
        (request: CopyAdoptionApprovalRequest)
        (custody: CopyAdoptionCustody)
        (registry: CopyAdoptionRegistryReceipt)
        (inspection: CopyAdoptionInspectionReceipt)
        observedAt
        =
        equal custody.CiphertextSha256 request.CiphertextSha256
        && equal inspection.CiphertextSha256 request.CiphertextSha256
        && custody.CiphertextBytes = request.CiphertextBytes
        && inspection.CiphertextBytes = request.CiphertextBytes
        && custody.ValidUntil >= observedAt
        && registry.ValidUntil >= observedAt
        && inspection.ValidUntil >= observedAt
        && request.ExpiresAt >= observedAt

    let private qualify
        (request: CopyAdoptionApprovalRequest)
        (submission: CopyAdoptionSubmission)
        (observedAt: DateTimeOffset)
        =
        match
            ManagedCopyAdoptionCustody.parse submission.Custodian.Canonical,
            ManagedCopyAdoptionRegistryReceipt.parse submission.Registry.Canonical,
            ManagedCopyAdoptionInspectionReceipt.parse submission.Inspection.Canonical
        with
        | Some custody, Some registry, Some inspection when
            identities request submission custody registry inspection
            && commitments request custody registry inspection
            && bytesAndWindow request custody registry inspection observedAt
            ->
            Some(
                [
                    custody.ValidUntil
                    registry.ValidUntil
                    inspection.ValidUntil
                    request.ExpiresAt
                ]
                |> List.min
            )
        | _ -> None

    let verify
        (request: CopyAdoptionApprovalRequest)
        (submission: CopyAdoptionSubmission)
        (observedAt: DateTimeOffset)
        (cancellationToken: CancellationToken)
        =
        try
            cancellationToken.ThrowIfCancellationRequested()

            match
                Environment.GetEnvironmentVariable("CLAIMCORE_COPY_LOCATION_MAPPING_FILE")
                |> Option.ofObj,
                Environment.GetEnvironmentVariable("CLAIMCORE_COPY_COMMITMENT_KEY_FILE")
                |> Option.ofObj,
                qualify request submission observedAt
            with
            | Some mappingPath, Some keyPath, Some expiresAt ->
                let expected: PrivateCopyExpectation =
                    {
                        CopyId = request.CopyId
                        CaseId = request.CaseId
                        CiphertextSha256 = request.CiphertextSha256
                        CiphertextBytes = request.CiphertextBytes
                        LocationCommitment = request.LocationCommitment
                        CustodianCommitment = request.CustodianCommitment
                    }

                DatabasePrivateCopyLocation.verify expected observedAt expiresAt mappingPath keyPath
            | _ -> None
        with _ ->
            None

[<Sealed>]
type internal DatabaseAdoptCopyLocation() =
    interface ICopyAdoptionPrivateLocation with
        member _.Verify(_, _, _, request, submission, observedAt, cancellationToken) =
            DatabaseAdoptCopyLocationProof.verify request submission observedAt cancellationToken
            |> Task.FromResult
