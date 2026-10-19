namespace ClaimCore.ContractGeneration

open System.IO
open System.Reflection
open System.Text
open ClaimCore.Wire

module ContractArtifacts =
    let private oracle name =
        use stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaimCore.Oracles." + name)
            |> nonNull

        use output = new MemoryStream()
        stream.CopyTo output
        output.ToArray()

    let all () =
        [
            {
                Name = "openapi.json"
                Bytes = OpenApi.emit ()
            }
            {
                Name = "client.ts"
                Bytes = TypeScript.source ()
            }
            {
                Name = "descriptors.json"
                Bytes = ContractDocuments.descriptors ()
            }
            {
                Name = "cli-frame.schema.json"
                Bytes = ContractDocuments.frames ()
            }
            {
                Name = "delivery-vectors.json"
                Bytes = oracle "delivery-vectors.json"
            }
            {
                Name = "correlation-vectors.json"
                Bytes = oracle "correlation-vectors.json"
            }
            {
                Name = "corpus.json"
                Bytes = SchemaSamples.corpus ()
            }
            {
                Name = "package.json"
                Bytes = Encoding.UTF8.GetBytes "{\"private\":true,\"type\":\"module\"}\n"
            }
        ]
