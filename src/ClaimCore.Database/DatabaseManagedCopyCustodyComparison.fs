namespace ClaimCore.Database

open System
open System.Security.Cryptography
open ClaimCore.HostSecurity
open ClaimCore.Postgres

[<NoEquality; NoComparison>]
type internal ManagedCopyLocationRow =
    {
        CopyId: Guid
        ProducerKind: string
        Kind: string
        SourceCaseId: Guid option
        CiphertextSha256: byte array
        CiphertextBytes: int64
        CustodianCommitment: byte array option
        LocationCommitment: byte array option
        State: string
    }

/// Exact private custody commitments come from the actual registered or adopted origin.
module internal DatabaseManagedCopyCustodyComparison =
    let private equalBytes (actual: byte array) (expected: byte array) =
        CryptographicOperations.FixedTimeEquals(actual, expected)

    let private ownerCustody
        (key: ManagedCopyCommitmentKey)
        (entry: CopyLocationEntry)
        (row: ManagedCopyLocationRow)
        =
        match
            entry.CustodianId, entry.Location, row.CustodianCommitment, row.LocationCommitment
        with
        | Some actor, Some path, Some custodian, Some location ->
            equalBytes custodian (key.Commit("custodian", actor))
            && equalBytes location (key.Commit("location", path))
        | _ -> false

    let private originProjectionMatches
        (row: ManagedCopyLocationRow)
        (origin: VerifiedCopyAdoptionOrigin)
        =
        match row.ProducerKind with
        | "PRODUCT_EXPORT" -> row.CustodianCommitment.IsNone && row.LocationCommitment.IsNone
        | "ADOPTED_EXTERNAL" ->
            row.CustodianCommitment |> Option.exists (equalBytes origin.CustodianCommitment)
            && (row.LocationCommitment |> Option.exists (equalBytes origin.LocationCommitment))
        | _ -> false

    let private adoptedCustody
        (key: ManagedCopyCommitmentKey)
        (entry: CopyLocationEntry)
        (row: ManagedCopyLocationRow)
        (origin: VerifiedCopyAdoptionOrigin)
        =
        match entry.CustodianId, entry.Location with
        | Some actor, Some path ->
            origin.CopyId = row.CopyId
            && Some origin.CaseId = row.SourceCaseId
            && origin.ProducerKind = row.ProducerKind
            && equalBytes origin.CiphertextSha256 row.CiphertextSha256
            && origin.CiphertextBytes = row.CiphertextBytes
            && equalBytes origin.CustodianCommitment (key.Commit("custodian", actor))
            && equalBytes origin.LocationCommitment (key.Commit("location", path))
            && originProjectionMatches row origin
        | _ -> false

    let matches key entry row origin =
        match row.ProducerKind, origin with
        | "OWNER_ATTESTED", None -> ownerCustody key entry row
        | "PRODUCT_EXPORT", None ->
            entry.CustodianId.IsNone
            && entry.Location.IsNone
            && row.CustodianCommitment.IsNone
            && row.LocationCommitment.IsNone
        | ("PRODUCT_EXPORT" | "ADOPTED_EXTERNAL"), Some verified ->
            adoptedCustody key entry row verified
        | _ -> false
