namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open ClaimCore.HostSecurity
open ClaimCore.Postgres

[<NoEquality; NoComparison>]
type internal PrivateCopyExpectation =
    {
        CopyId: Guid
        CaseId: Guid
        CiphertextSha256: byte array
        CiphertextBytes: int64
        LocationCommitment: byte array
        CustodianCommitment: byte array
    }

/// Both publication and adoption prove the same owner-private, handle-first ciphertext fact.
module internal DatabasePrivateCopyLocation =
    let private equal (left: byte array) (right: byte array) =
        left.Length = right.Length
        && CryptographicOperations.FixedTimeEquals(left, right)

    let private mapped (expected: PrivateCopyExpectation) mappingPath =
        match DatabaseAdoptCopyInputs.mapping mappingPath with
        | Some(copyId, caseId, custodianId, location) when
            copyId = expected.CopyId
            && caseId = expected.CaseId
            && Path.IsPathFullyQualified(location)
            && not (location.Split(Path.DirectorySeparatorChar) |> Array.contains "..")
            ->
            Some(custodianId, location)
        | _ -> None

    let private hash
        (expected: PrivateCopyExpectation)
        (observedAt: DateTimeOffset)
        (expiresAt: DateTimeOffset)
        (locationCommitment: byte array)
        (custodianCommitment: byte array)
        location
        =
        match PrivateFileService.hashPrivateFile expected.CiphertextBytes location with
        | Ok(length, digest) ->
            try
                if
                    length <> expected.CiphertextBytes
                    || not (equal digest expected.CiphertextSha256)
                then
                    None
                else
                    VerifiedCopyAdoptionPrivateLocation.FromVerifiedOpenFile(
                        expected.CopyId,
                        expected.CaseId,
                        locationCommitment,
                        custodianCommitment,
                        digest,
                        length,
                        observedAt,
                        expiresAt
                    )
                    |> Some
            finally
                CryptographicOperations.ZeroMemory(digest)
        | Error _ -> None

    let verify expected observedAt expiresAt mappingPath keyPath =
        if expiresAt <= observedAt then
            None
        else
            try
                match mapped expected mappingPath with
                | None -> None
                | Some(custodianId, location) ->
                    use key = ManagedCopyCommitmentKey.Load(keyPath)
                    let locationCommitment = key.Commit("location", location)
                    let custodianCommitment = key.Commit("custodian", custodianId)

                    try
                        if
                            not (equal locationCommitment expected.LocationCommitment)
                            || not (equal custodianCommitment expected.CustodianCommitment)
                        then
                            None
                        else
                            hash
                                expected
                                observedAt
                                expiresAt
                                locationCommitment
                                custodianCommitment
                                location
                    finally
                        CryptographicOperations.ZeroMemory(locationCommitment)
                        CryptographicOperations.ZeroMemory(custodianCommitment)
            with _ ->
                None
