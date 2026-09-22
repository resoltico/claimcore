namespace ClaimCore.Contracts

module TransportDiagnosticSchemas =
    let private property = WireSchema.property
    let private token = WireSchema.token
    let private nullable = Schema.nullable

    let private diagnostic identifiers parameters =
        WireSchema.objectOf
            [
                property "id" (WireSchema.enumeration identifiers)
                property "parameters" parameters
            ]

    let webStartup =
        WebStartupDiagnostics.all
        |> List.groupBy (fun reason ->
            match reason with
            | WebStartupProblem.MissingSetting _
            | WebStartupProblem.InvalidSetting _ -> true
            | _ -> false)
        |> List.map (fun (hasSetting, reasons) ->
            let parameters =
                if hasSetting then
                    WireSchema.objectOf
                        [
                            property
                                "setting"
                                (WebSettings.all
                                 |> List.map WebSettings.token
                                 |> WireSchema.enumeration)
                        ]
                else
                    WireSchema.objectOf []

            WireSchema.objectOf
                [
                    property "kind" (token "webProcessFailure")
                    property
                        "diagnostic"
                        (diagnostic
                            (reasons |> List.map WebStartupDiagnostics.token |> List.distinct)
                            parameters)
                    property "message" WireSchema.text
                    property "recommendedAction" (token "STOP_AND_INVESTIGATE")
                ])
        |> Schema.oneOf

    let private processVariant reason phase result changed context =
        WireSchema.objectOf
            [
                property "kind" (token "cliProcessFailure")
                property
                    "diagnostic"
                    (diagnostic [ CliProcessDiagnostics.token reason ] (WireSchema.objectOf []))
                property "message" WireSchema.text
                property "deliveryPhase" (token phase)
                property "potentiallyChanged" changed
                property "resultExitCode" result
                property
                    "operationId"
                    (if context then
                         nullable WireSchema.uuid
                     else
                         Schema.nullValue)
                property
                    "requestSha256"
                    (if context then
                         nullable WireSchema.digest
                     else
                         Schema.nullValue)
                property "recommendedAction" (token "STOP_AND_INVESTIGATE")
            ]

    let cliProcess =
        [
            CliProcessProblem.UnsupportedInvocation,
            "IDLE",
            Schema.nullValue,
            Schema.constant (BooleanConstant false),
            false
            CliProcessProblem.UnexpectedFailure,
            "IDLE",
            Schema.nullValue,
            Schema.constant (BooleanConstant false),
            false
            CliProcessProblem.InputReadFailed,
            "READING",
            Schema.nullValue,
            Schema.constant (BooleanConstant false),
            false
            CliProcessProblem.RuntimeAcquireFailed,
            "ACQUIRING_RUNTIME",
            Schema.nullValue,
            Schema.constant (BooleanConstant false),
            false
            CliProcessProblem.DispatchFailed,
            "DISPATCH_UNCONFIRMED",
            Schema.nullValue,
            Schema.boolean,
            true
            CliProcessProblem.SerializationFailed,
            "RESULT_OBSERVED",
            Schema.nullValue,
            Schema.boolean,
            true
            CliProcessProblem.OutputWriteFailed,
            "RESULT_AVAILABLE",
            ([ 0L; 1L; 2L; 3L; 4L; 64L; 70L; 130L ]
             |> List.map IntegerConstant
             |> Schema.enumeration),
            Schema.boolean,
            true
        ]
        |> List.map (fun (reason, phase, result, changed, context) ->
            processVariant reason phase result changed context)
        |> Schema.oneOf

    let private render name schema =
        let document =
            {
                Identifier = "https://claimcore.local/contracts/" + name
                Title = name
                Root = schema
                Definitions = []
            }

        CanonicalJson.renderSchema document schema

    let cliProcessDocument = render "cli-v3.process-failure.schema.json" cliProcess
    let webStartupDocument = render "web-v2.process-failure.schema.json" webStartup
