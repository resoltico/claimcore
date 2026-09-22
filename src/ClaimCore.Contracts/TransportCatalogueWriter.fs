namespace ClaimCore.Contracts

open System.Text.Json

module internal TransportCatalogueWriter =
    let document (schema: Schema) =
        {
            Identifier = "https://claimcore.local/contracts/endpoint.schema.json"
            Title = "ClaimCore endpoint input"
            Root = schema
            Definitions = []
        }

    let write (writer: Utf8JsonWriter) schemas =
        writer.WriteStartObject("diagnostics")

        for name, schema in schemas do
            writer.WritePropertyName(name: string)

            use parsed =
                System.Text.Json.JsonDocument.Parse(
                    CanonicalContract.bytes (CanonicalJson.renderSchema (document schema) schema)
                )

            parsed.RootElement.WriteTo writer

        writer.WriteEndObject()
