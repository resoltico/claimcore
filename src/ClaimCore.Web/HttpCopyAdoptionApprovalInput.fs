namespace ClaimCore.Web

open System
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts
open HttpInputSupport

module internal HttpCopyAdoptionApprovalInput =
    let private uuid value =
        value |> stringValue |> operationIdValue

    let private number value =
        value |> stringValue |> canonicalNonNegativeInt64

    let private digest value =
        value |> stringValue |> digestValue |> Convert.FromHexString

    let private instant value =
        value |> stringValue |> utcMicrosecondTimestampValue

    let private origin (element: JsonElement) =
        let values = properties element

        match required "kind" values |> stringValue with
        | "PRODUCT_EXPORT" ->
            exactProperties [ "kind"; "exportId"; "receiptSequence"; "receiptHash" ] values
            |> ignore

            CopyAdoptionOrigin.ProductExport(
                required "exportId" values |> uuid,
                required "receiptSequence" values |> number,
                required "receiptHash" values |> digest
            )
        | "ADOPTED_EXTERNAL" ->
            exactProperties [ "kind"; "registrySequence"; "registryHash" ] values |> ignore

            CopyAdoptionOrigin.AdoptedExternal(
                required "registrySequence" values |> number,
                required "registryHash" values |> digest
            )
        | _ -> fail HttpInputProblem.InvalidJson

    let approve bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties
                    [
                        "approvalId"
                        "adoptionEventId"
                        "copyId"
                        "caseId"
                        "origin"
                        "ciphertextSha256"
                        "ciphertextBytes"
                        "capturedAt"
                        "retainUntil"
                        "locationCommitment"
                        "custodianCommitment"
                        "custodianSigningKeyId"
                        "registrySigningKeyId"
                        "inspectorSigningKeyId"
                        "custodianCanonicalSha256"
                        "registryCanonicalSha256"
                        "inspectionReportSha256"
                        "expiresAt"
                    ]

            {
                ApprovalId = required "approvalId" values |> uuid
                AdoptionEventId = required "adoptionEventId" values |> uuid
                CopyId = required "copyId" values |> uuid
                CaseId = required "caseId" values |> uuid
                Origin = required "origin" values |> origin
                CiphertextSha256 = required "ciphertextSha256" values |> digest
                CiphertextBytes = required "ciphertextBytes" values |> number
                CapturedAt = required "capturedAt" values |> instant
                RetainUntil = required "retainUntil" values |> instant
                LocationCommitment = required "locationCommitment" values |> digest
                CustodianCommitment = required "custodianCommitment" values |> digest
                CustodianSigningKeyId = required "custodianSigningKeyId" values |> uuid
                RegistrySigningKeyId = required "registrySigningKeyId" values |> uuid
                InspectorSigningKeyId = required "inspectorSigningKeyId" values |> uuid
                CustodianCanonicalSha256 = required "custodianCanonicalSha256" values |> digest
                RegistryCanonicalSha256 = required "registryCanonicalSha256" values |> digest
                InspectionReportSha256 = required "inspectionReportSha256" values |> digest
                ExpiresAt = required "expiresAt" values |> instant
            })
