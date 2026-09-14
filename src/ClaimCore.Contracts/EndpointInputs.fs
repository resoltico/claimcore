namespace ClaimCore.Contracts

open ClaimCore.Application

module internal EndpointInputs =
    let digest = Schema.string None (Some "^[0-9a-f]{64}$") (Some 64) (Some 64)
    let absolutePath = Schema.string None None (Some 1) None

    let private caseReferenceField (semantic: SemanticCoreContract) =
        semantic.Fields |> List.find (fun field -> field.Name = "caseReference")

    let caseReference (semantic: SemanticCoreContract) =
        Schema.objectOf
            false
            [
                Schema.property
                    "caseReference"
                    (ScalarSchemas.scalar (caseReferenceField semantic).Scalar)
                    true
            ]

    let history (semantic: SemanticCoreContract) =
        Schema.objectOf
            false
            [
                Schema.property
                    "caseReference"
                    (ScalarSchemas.scalar (caseReferenceField semantic).Scalar)
                    true
                Schema.property "cursor" (Schema.string None None (Some 1) None) false
                Schema.property
                    "limit"
                    (Schema.integer (Some 1L) (Some(int64 semantic.MaximumPageSize)))
                    true
                Schema.property
                    "detail"
                    (Schema.enumeration [ TextConstant "SUMMARY"; TextConstant "FULL" ])
                    true
            ]

    let operation =
        Schema.objectOf false [ Schema.property "operationId" ScalarSchemas.uuid true ]

    let cursor maximumPageSize =
        Schema.objectOf
            false
            [
                Schema.property "cursor" (Schema.string None None (Some 1) None) false
                Schema.property
                    "limit"
                    (Schema.integer (Some 1L) (Some(int64 maximumPageSize)))
                    true
            ]

    let recoveryResolution =
        Schema.objectOf
            false
            [
                Schema.property "operationId" ScalarSchemas.uuid true
                Schema.property "requestSha256" digest true
            ]

    let recoveryDismiss =
        Schema.objectOf
            false
            [
                Schema.property "operationId" ScalarSchemas.uuid true
                Schema.property "requestSha256" digest true
                Schema.property "confirmed" (Schema.constant (BooleanConstant true)) true
            ]

    let recoveryExportTarget =
        Schema.objectOf
            false
            [
                Schema.property "operationId" ScalarSchemas.uuid true
                Schema.property "requestSha256" digest true
            ]

    let recoveryExport =
        Schema.objectOf
            false
            [
                Schema.property "operationId" ScalarSchemas.uuid true
                Schema.property "requestSha256" digest true
                Schema.property "destination" absolutePath true
            ]

    let importPreview =
        Schema.objectOf false [ Schema.property "source" absolutePath true ]

    let importRetain =
        Schema.objectOf
            false
            [
                Schema.property "source" absolutePath true
                Schema.property "sourceSha256" digest true
                Schema.property "confirmed" (Schema.constant (BooleanConstant true)) true
            ]
