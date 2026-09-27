namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ClaimCore.Postgres

/// Publication observes actual private bytes; signed metadata alone is never custody proof.
module internal DatabaseExternalPublicationLocationProof =
    let private equal (left: byte array) (right: byte array) =
        left.Length = right.Length
        && CryptographicOperations.FixedTimeEquals(left, right)

    let private matches
        (supplied: ExternalCopyPublication)
        (parsed: ExternalCopyPublication)
        (inspection: ExternalCopyInspection)
        observedAt
        =
        supplied.PublicationId = parsed.PublicationId
        && supplied.CopyId = parsed.CopyId
        && supplied.CaseId = parsed.CaseId
        && equal supplied.CiphertextSha256 parsed.CiphertextSha256
        && supplied.CiphertextBytes = parsed.CiphertextBytes
        && equal supplied.LocationCommitment parsed.LocationCommitment
        && equal supplied.CustodianCommitment parsed.CustodianCommitment
        && inspection.ObservedAt <= observedAt
        && parsed.ValidUntil > observedAt
        && inspection.ValidUntil > observedAt

    let verify
        (publication: ExternalCopyPublication)
        (submission: ExternalCopyPublicationSubmission)
        (observedAt: DateTimeOffset)
        (cancellationToken: CancellationToken)
        =
        try
            cancellationToken.ThrowIfCancellationRequested()

            match
                ManagedCopyExternalPublicationDocuments.parse submission,
                Environment.GetEnvironmentVariable("CLAIMCORE_COPY_LOCATION_MAPPING_FILE")
                |> Option.ofObj,
                Environment.GetEnvironmentVariable("CLAIMCORE_COPY_COMMITMENT_KEY_FILE")
                |> Option.ofObj
            with
            | Some(parsed, inspection), Some mappingPath, Some keyPath when
                matches publication parsed inspection observedAt
                ->
                let expected: PrivateCopyExpectation =
                    {
                        CopyId = publication.CopyId
                        CaseId = publication.CaseId
                        CiphertextSha256 = publication.CiphertextSha256
                        CiphertextBytes = publication.CiphertextBytes
                        LocationCommitment = publication.LocationCommitment
                        CustodianCommitment = publication.CustodianCommitment
                    }

                let expiresAt = min parsed.ValidUntil inspection.ValidUntil
                DatabasePrivateCopyLocation.verify expected observedAt expiresAt mappingPath keyPath
            | _ -> None
        with _ ->
            None

[<Sealed>]
type internal DatabaseExternalPublicationLocation() =
    interface IExternalCopyPublicationPrivateLocation with
        member _.Verify(_, _, publication, submission, observedAt, cancellationToken) =
            DatabaseExternalPublicationLocationProof.verify
                publication
                submission
                observedAt
                cancellationToken
            |> Task.FromResult
