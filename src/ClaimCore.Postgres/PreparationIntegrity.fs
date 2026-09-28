namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.RecordFormat

/// One defensive authority for the exact canonical request retained by recovery storage.
module internal PreparationIntegrity =
    let private validDigest (value: string) =
        value.Length = 64
        && value
           |> Seq.forall (fun character ->
               ('0' <= character && character <= '9') || ('a' <= character && character <= 'f'))

    let private digest (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexStringLower

    let private request
        (operationId: Guid)
        (canonicalRequestFormat: int)
        (requestSha256: string)
        (canonicalRequest: byte array)
        =
        if operationId = Guid.Empty then
            Error "operationId"
        elif canonicalRequestFormat <> RecordVersions.CanonicalCommandFormat then
            Error "canonicalRequestFormat"
        elif canonicalRequest.Length = 0 then
            Error "canonicalRequest"
        elif canonicalRequest.Length > SemanticContract.current.RequestByteLimit then
            Error "canonicalRequest"
        elif not (validDigest requestSha256) || digest canonicalRequest <> requestSha256 then
            Error "requestSha256"
        else
            match
                RequestRecord.decode SemanticContract.current.RequestByteLimit canonicalRequest
            with
            | Error _ -> Error "canonicalRequest"
            | Ok decoded when
                decoded.OperationId <> operationId
                || not (
                    CryptographicOperations.FixedTimeEquals(
                        RequestRecord.encode decoded,
                        canonicalRequest
                    )
                )
                ->
                Error "canonicalRequest"
            | Ok decoded -> Ok decoded

    let private semanticFingerprint () =
        SemanticContract.fingerprint SemanticContract.current
        |> SemanticCoreFingerprint.value

    let private provenance
        requiresCurrentSemanticFingerprint
        applicationVersion
        contractFingerprint
        (kind: PreparingContractKind)
        =
        if
            String.IsNullOrWhiteSpace(applicationVersion)
            || applicationVersion.Length > 200
            || applicationVersion <> applicationVersion.Trim()
        then
            Error "preparingApplicationVersion"
        elif not (validDigest contractFingerprint) then
            Error "preparingContractFingerprint"
        else
            match kind with
            | PreparingContractKind.SemanticCoreV1 when
                requiresCurrentSemanticFingerprint
                && contractFingerprint <> semanticFingerprint ()
                ->
                Error "preparingContractFingerprint"
            | _ -> Ok()

    let validateIdentity (draft: RecoveryPreparationDraft) =
        request
            draft.OperationId
            draft.CanonicalRequestFormat
            draft.RequestSha256
            draft.CanonicalRequest
        |> Result.mapError RecoveryStoreFailure.InvalidInput

    let validateDraft (draft: RecoveryPreparationDraft) =
        match validateIdentity draft with
        | Error failure -> Error failure
        | Ok _ when draft.CaseId = Guid.Empty -> Error(RecoveryStoreFailure.InvalidInput "caseId")
        | Ok _ when draft.PreparerActorId = Guid.Empty ->
            Error(RecoveryStoreFailure.InvalidInput "preparerActorId")
        | Ok _ when draft.ImporterActorId = Some Guid.Empty ->
            Error(RecoveryStoreFailure.InvalidInput "importerActorId")
        | Ok _ when draft.PreparerGrantRevision <= 0L ->
            Error(RecoveryStoreFailure.InvalidInput "preparerGrantRevision")
        | Ok _ ->
            provenance
                true
                draft.PreparingApplicationVersion
                draft.PreparingContractFingerprint
                draft.PreparingContractKind
            |> Result.mapError RecoveryStoreFailure.InvalidInput

    let verify (preparation: RetainedPreparation) =
        match
            request
                preparation.OperationId
                preparation.CanonicalRequestFormat
                preparation.RequestSha256
                preparation.CanonicalRequest,
            provenance
                false
                preparation.PreparingApplicationVersion
                preparation.PreparingContractFingerprint
                preparation.PreparingContractKind
        with
        | Ok decoded, Ok() when
            preparation.CaseId <> Guid.Empty
            && preparation.PreparerActorId <> Guid.Empty
            && preparation.ImporterActorId <> Some Guid.Empty
            && preparation.PreparerGrantRevision > 0L
            ->
            Ok decoded
        | _ -> Error RecoveryStoreFailure.StoreCorrupt
