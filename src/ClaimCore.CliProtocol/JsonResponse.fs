namespace ClaimCore.Cli

open ClaimCore.Contracts

/// CLI process delivery metadata around a Contracts-owned canonical response body.
type RenderedResponse = CliWireResponse

module JsonResponse =
    let bytes (response: RenderedResponse) = response.Bytes

    let protocolFailure exitCode (failure: ProtocolFailure) =
        CliWireCodec.protocolFailure exitCode failure
