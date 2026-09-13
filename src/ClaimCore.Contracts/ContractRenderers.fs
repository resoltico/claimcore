namespace ClaimCore.Contracts

open System
open System.Security.Cryptography

module ContractRenderers =
    let semantic (projection: ContractModel) =
        ContractJson.semanticContract projection.Semantic

    let semanticSchema (projection: ContractModel) =
        CanonicalJson.renderSchema projection.DefinitionSchema projection.DefinitionSchema.Root

    let cli (projection: ContractModel) = ContractJson.cliContract projection
    let web (projection: ContractModel) = ContractJson.webContract projection

    let semanticFingerprint projection =
        ClaimCore.Application.SemanticContract.fingerprint projection.Semantic

    let cliFingerprint projection =
        cli projection
        |> CanonicalContract.bytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower
        |> CliWireContractFingerprint

    let webFingerprint projection =
        web projection
        |> CanonicalContract.bytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower
        |> WebWireContractFingerprint
