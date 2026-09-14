// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal EndpointBinding15 =
    let value =
        RawEndpoint(
            {
                Identifier = "recovery.importEnvelopePreview"
                Method = "POST"
                Path = "/api/v2/recovery/import-envelope/preview"
                Body = RequestBody.Raw("application/vnd.claimcore.recovery\u002Bjson", 131072, [])
                SuccessMediaType = None
            },
            JsonCodec<SessionLogoutRequest>(
                (SessionLogoutRequestJson.read),
                (SessionLogoutRequestJson.write)
            ),
            JsonCodec<RecoveryImportEnvelopePreviewResponse>(
                (RecoveryImportEnvelopePreviewResponseJson.read),
                (RecoveryImportEnvelopePreviewResponseJson.write)
            )
        )

module internal EndpointBinding16 =
    let value =
        RawEndpoint(
            {
                Identifier = "recovery.importEnvelopeRetain"
                Method = "POST"
                Path = "/api/v2/recovery/import-envelope/retain"
                Body =
                    RequestBody.Raw(
                        "application/vnd.claimcore.recovery\u002Bjson",
                        131072,
                        [ "X-ClaimCore-Source-Sha256" ]
                    )
                SuccessMediaType = None
            },
            JsonCodec<RecoveryImportEnvelopeRetainHeaders>(
                (RecoveryImportEnvelopeRetainHeadersJson.read),
                (RecoveryImportEnvelopeRetainHeadersJson.write)
            ),
            JsonCodec<RecoveryImportEnvelopeRetainResponse>(
                (RecoveryImportEnvelopeRetainResponseJson.read),
                (RecoveryImportEnvelopeRetainResponseJson.write)
            )
        )

module internal EndpointBinding17 =
    let value =
        RawEndpoint(
            {
                Identifier = "recovery.importRecordPreview"
                Method = "POST"
                Path = "/api/v2/recovery/import-record/preview"
                Body =
                    RequestBody.Raw(
                        "application/vnd.claimcore.canonical-command\u002Bjson",
                        65536,
                        []
                    )
                SuccessMediaType = None
            },
            JsonCodec<SessionLogoutRequest>(
                (SessionLogoutRequestJson.read),
                (SessionLogoutRequestJson.write)
            ),
            JsonCodec<RecoveryImportRecordPreviewResponse>(
                (RecoveryImportRecordPreviewResponseJson.read),
                (RecoveryImportRecordPreviewResponseJson.write)
            )
        )

module internal EndpointBinding18 =
    let value =
        RawEndpoint(
            {
                Identifier = "recovery.importRecordRetain"
                Method = "POST"
                Path = "/api/v2/recovery/import-record/retain"
                Body =
                    RequestBody.Raw(
                        "application/vnd.claimcore.canonical-command\u002Bjson",
                        65536,
                        [ "X-ClaimCore-Source-Sha256" ]
                    )
                SuccessMediaType = None
            },
            JsonCodec<RecoveryImportEnvelopeRetainHeaders>(
                (RecoveryImportEnvelopeRetainHeadersJson.read),
                (RecoveryImportEnvelopeRetainHeadersJson.write)
            ),
            JsonCodec<RecoveryImportRecordRetainResponse>(
                (RecoveryImportRecordRetainResponseJson.read),
                (RecoveryImportRecordRetainResponseJson.write)
            )
        )
