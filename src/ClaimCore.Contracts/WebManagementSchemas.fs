namespace ClaimCore.Contracts

open ClaimCore.Application
open ClaimCore.Domain

/// Exact actor-administration wire shapes. The caller owns a stable event ID across uncertain
/// retries; neither the browser nor server can substitute a fresh identity after dispatch.
module internal WebManagementSchemas =
    let private issuer = Schema.string (Some "uri") None (Some 9) (Some 2048)
    let private subject = Schema.string None None (Some 1) (Some 512)
    let private clientId = Schema.string None None (Some 1) (Some 256)

    let private caseReference =
        FieldDefinitions.scalar "caseReference" |> ScalarSchemas.scalar

    let private principal =
        Schema.oneOf
            [
                WireSchema.kind
                    "HUMAN"
                    [ WireSchema.property "issuer" issuer; WireSchema.property "subject" subject ]
                WireSchema.kind
                    "SERVICE"
                    [ WireSchema.property "issuer" issuer; WireSchema.property "clientId" clientId ]
            ]

    let private scope =
        Schema.oneOf
            [
                WireSchema.kind "INSTALLATION" []
                WireSchema.kind "CASE" [ WireSchema.property "caseReference" caseReference ]
            ]

    let private role =
        WireSchema.enumeration
            [
                "OWNER"
                "CASE_READER"
                "CASE_EDITOR"
                "RECOVERY_OPERATOR"
                "RECOVERY_EXPORTER"
                "AUDITOR_CUSTODIAN"
                "DATA_STEWARD"
            ]

    let private request properties =
        WireSchema.objectOf (WireSchema.property "eventId" WireSchema.uuid :: properties)

    let register = request [ WireSchema.property "principal" principal ]

    let setGrant =
        request
            [
                WireSchema.property "principal" principal
                WireSchema.property "role" role
                WireSchema.property "scope" scope
                WireSchema.property "active" Schema.boolean
            ]

    let setEnabled =
        request
            [
                WireSchema.property "principal" principal
                WireSchema.property "enabled" Schema.boolean
            ]

    let observe = request []

    let private outcome tag data =
        WireSchema.objectOf
            [
                WireSchema.property "tag" (WireSchema.token tag)
                WireSchema.property "data" data
            ]

    let response endpoint =
        WireSchema.objectOf
            [
                WireSchema.property "endpoint" (WireSchema.token endpoint)
                WireSchema.property
                    "outcome"
                    (Schema.oneOf
                        [
                            outcome
                                "APPLIED"
                                (WireSchema.objectOf
                                    [
                                        WireSchema.property "eventId" WireSchema.uuid
                                        WireSchema.property "grantRevision" WireSchema.revision
                                        WireSchema.property "targetActorId" WireSchema.uuid
                                    ])
                            outcome "RESOURCE_UNAVAILABLE" Schema.nullValue
                            outcome
                                "UNCONFIRMED"
                                (WireSchema.objectOf
                                    [ WireSchema.property "eventId" WireSchema.uuid ])
                        ])
            ]
