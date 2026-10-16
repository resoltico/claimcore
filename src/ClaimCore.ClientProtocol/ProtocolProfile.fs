namespace ClaimCore.ClientProtocol

open System.Text.Json

/// Explicit lexical admission properties not expressed by ordinary JSON Schema.
module ProtocolProfile =
    [<Literal>]
    let MaximumDepth = 32

    [<Literal>]
    let MaximumResponseBytes = 16 * 1024 * 1024

    [<Literal>]
    let MaximumInvocationBytes = 128 * 1024

    [<Literal>]
    let CanonicalTimestampPattern =
        "^(?!0000-)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{6}0\\+00:00$"

    let strictBytes =
        {|
            utf8 = "fatal"
            duplicateDecodedMembers = "reject"
            unicode = "scalars-without-nul-or-noncharacters"
            integerLexemes = "minus-optional-digits-no-fraction-or-exponent"
            maximumDepth = MaximumDepth
            maximumResponseBytes = MaximumResponseBytes
            maximumInvocationBytes = MaximumInvocationBytes
        |}

    let strictBytesJson () =
        JsonSerializer.SerializeToUtf8Bytes strictBytes

    let deliveryJson () =
        JsonSerializer.SerializeToUtf8Bytes
            {|
                outcomes = DeliveryPolicy.outcomePolicies
                missing = DeliveryPolicy.missingPolicies
                hosts = DeliveryPolicy.hostPolicies
                resolutions = DeliveryPolicy.resolutionPolicies
                evidence = DeliveryPolicy.evidencePolicies
                unresolvedKnowledge = DeliveryPolicy.unresolvedKnowledge
                contentExcludedDiagnostic = DeliveryPolicy.contentExcludedDiagnostic
                correlationRules = CorrelationPolicy.rules
                correlationSelectors = CorrelationPolicy.selectors
                correlationTagPath = CorrelationPolicy.tagPath
            |}
