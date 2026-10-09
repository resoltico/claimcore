namespace ClaimCore.ContractGeneration

open System.Text
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts

/// Public build metadata has no installation calendar or actor authority.
module internal ProductMetadataArtifact =
    let bytes (projection: ContractModel) =
        let definition =
            ContractRenderers.semantic projection |> CanonicalContract.text |> _.TrimEnd()

        let version = JsonSerializer.Serialize(BuildIdentity.current.Version)

        let semantic =
            ContractRenderers.semanticFingerprint projection
            |> SemanticCoreFingerprint.value
            |> JsonSerializer.Serialize

        let web =
            ContractRenderers.webFingerprint projection
            |> WebWireContractFingerprint.value
            |> JsonSerializer.Serialize

        let source =
            $"""/* Generated from ClaimCore.Contracts. Do not edit. */
import type {{ SemanticDefinition }} from "./web-v3.types.semantic";
export type PublicDefinition = Readonly<{{
  definition: SemanticDefinition;
  productVersion: string;
  semanticFingerprint: string;
  webFingerprint: string;
}}>;
export const productMetadata: PublicDefinition = {{
  definition: {definition},
  productVersion: {version},
  semanticFingerprint: {semantic},
  webFingerprint: {web},
}};
        """

        Encoding.UTF8.GetBytes(source.TrimEnd() + "\n")
