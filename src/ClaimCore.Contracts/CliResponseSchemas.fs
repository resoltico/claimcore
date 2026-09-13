namespace ClaimCore.Contracts

open ClaimCore.Application

[<RequireQualifiedAccess>]
module CliResponseSchemas =
    let all (semantic: SemanticCoreContract) =
        [
            "command.prepare", CliResponseMutationSchemas.prepare semantic
            "command.execute", CliResponseMutationSchemas.submission semantic
            "case.get", CliResponseQuerySchemas.caseGet semantic
            "case.list", CliResponseQuerySchemas.caseList
            "case.history", CliResponseQuerySchemas.history semantic
            "operation.observe", CliResponseQuerySchemas.observe semantic
            "recovery.list", CliResponseQuerySchemas.recoveryList
            "recovery.inspect", CliResponseQuerySchemas.recoveryInspect semantic
            "recovery.resolve", CliResponseMutationSchemas.resolve semantic
            "recovery.dismiss", CliResponseMutationSchemas.dismiss semantic
            "recovery.export", CliResponseRecoverySchemas.export
            "recovery.importEnvelopePreview", CliResponseRecoverySchemas.importPreview semantic
            "recovery.importEnvelopeRetain", CliResponseRecoverySchemas.importRetain semantic
            "recovery.importRecordPreview", CliResponseRecoverySchemas.importPreview semantic
            "recovery.importRecordRetain", CliResponseRecoverySchemas.importRetain semantic
        ]

    let protocolFailure =
        WireSchema.objectOf
            [
                WireSchema.property "protocolVersion" (WireSchema.number 3)
                WireSchema.property "kind" (WireSchema.token "protocolFailure")
                WireSchema.property "code" WireSchema.text
                WireSchema.property "message" WireSchema.text
                WireSchema.property "path" (Schema.string None None (Some 0) None)
            ]
