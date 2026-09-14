namespace ClaimCore.Contracts

open System.Text
open DotNetModel

/// Generated .NET bindings consume the same static schema graph as the browser artifacts.
module DotNetProtocol =
    let private header =
        [
            "// Generated from ClaimCore.Contracts. Do not edit."
            "namespace ClaimCore.Protocol"
            ""
        ]

    let private bytes lines =
        String.concat "\n" (lines @ [ "" ]) |> Encoding.UTF8.GetBytes

    let private files prefix declarations =
        declarations
        |> List.mapi (fun i body ->
            let name =
                prefix
                + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)
                + ".fs"

            name, bytes (header @ [ "open System.Text.Json"; "" ] @ body))

    let private codec model name schema =
        "JsonCodec<"
        + typeName model name schema
        + ">(("
        + DotNetExpressions.read model name schema
        + "), ("
        + DotNetExpressions.write model name schema
        + "))"

    let private description (endpoint: WebEndpoint) =
        let body =
            match endpoint.Body with
            | None -> "RequestBody.None"
            | Some(JsonBody _) -> "RequestBody.Json"
            | Some(RawBody(media, maximum, headers)) ->
                "RequestBody.Raw ("
                + literal media
                + ", "
                + string maximum
                + ", "
                + strings (headers |> List.map fst)
                + ")"

        "{ Identifier = "
        + literal endpoint.Identifier
        + "; Method = "
        + literal endpoint.Method
        + "; Path = "
        + literal endpoint.Path
        + "; Body = "
        + body
        + "; SuccessMediaType = "
        + option literal endpoint.SuccessMediaType
        + " }"

    let private binding model (endpoint: WebEndpoint) =
        let name = identifier endpoint.Identifier
        let memberName = name.Substring(0, 1).ToLowerInvariant() + name.Substring(1)
        let response = codec model (name + "Response") endpoint.Response

        let constructor =
            match endpoint.Body with
            | None -> "ReadEndpoint(" + description endpoint + ", " + response + ")"
            | Some(JsonBody schema) ->
                "JsonEndpoint("
                + description endpoint
                + ", "
                + codec model (name + "Request") schema
                + ", "
                + response
                + ")"
            | Some(RawBody(_, _, headers)) ->
                let schema =
                    Schema.objectOf
                        false
                        (headers
                         |> List.map (fun (name, schema) -> Schema.property name schema true))

                "RawEndpoint("
                + description endpoint
                + ", "
                + codec model (name + "Headers") schema
                + ", "
                + response
                + ")"

        memberName, "    let " + memberName + " = " + constructor

    let private bindings model projection =
        let members = projection.WebEndpoints |> List.map (binding model)

        let definitions =
            WebSchemaDefinitions.all projection.Semantic projection.DefinitionSchema.Root
            |> Map.ofList

        let hostFailure = codec model "HostFailure" (Map.find "HostFailure" definitions)

        header
        @ [ "[<RequireQualifiedAccess>]"; "module WebV2 =" ]
        @ [
            "    let wireFingerprint = "
            + literal (
                ContractRenderers.webFingerprint projection |> WebWireContractFingerprint.value
            )
            "    let hostFailureStatuses = [ "
            + (WebSchemaDefinitions.hostFailureStatuses
               |> List.map string
               |> String.concat "; ")
            + " ]"
            "    let hostFailure = " + hostFailure
            ""
        ]
        @ (members
           |> List.mapi (fun i (name, _) ->
               "    let " + name + " = EndpointBinding" + string i + ".value"))
        @ [ "    let endpoints ="; "        [" ]
        @ (members |> List.map (fun (name, _) -> "            " + name + ".Description"))
        @ [ "        ]" ]

    let private scalar node =
        let valueType = DotNetModel.scalarType node.Schema |> Option.get

        [
            "module internal " + node.Name + " ="
            "    let read (value: JsonElement) : "
            + valueType
            + " = ("
            + DotNetScalarExpressions.read node.Schema
            + ") value"
            "    let write (writer: Utf8JsonWriter) (value: "
            + valueType
            + ") = ("
            + DotNetScalarExpressions.write node.Schema
            + ") writer value"
        ]

    let artifacts projection =
        let model = DotNetModel.build projection
        let types = model.Nodes |> List.map (DotNetNodes.declaration model) |> files "Types"
        let scalars = model.Scalars |> List.map scalar |> files "Scalar"
        let codecs = model.Nodes |> List.map (DotNetNodes.codec model) |> files "Codec"

        let definition =
            header
            @ [
                "open System.Text.Json"
                ""
                "module internal ProtocolDefinition ="
                "    let expected ="
                "        use document = JsonDocument.Parse("
                + literal (ContractRenderers.semantic projection |> CanonicalContract.text)
                + ")"
                "        document.RootElement.Clone()"
            ]
            |> bytes

        let endpoints =
            projection.WebEndpoints
            |> List.mapi (fun i endpoint ->
                let _, value = binding model endpoint

                [
                    "module internal EndpointBinding" + string i + " ="
                    "    let value = "
                    + value.Substring(value.IndexOf(" = ", System.StringComparison.Ordinal) + 3)
                ])
            |> files "Binding"

        let source =
            types
            @ [ "Definition.fs", definition ]
            @ scalars
            @ codecs
            @ endpoints
            @ [ "WebV2.fs", bindings model projection |> bytes ]

        let compile =
            [ "<Project>"; "  <ItemGroup>" ]
            @ (source
               |> List.map (fun (name, _) ->
                   "    <Compile Include=\"$(MSBuildThisFileDirectory)" + name + "\" />"))
            @ [ "  </ItemGroup>"; "</Project>" ]

        source @ [ "Protocol.Generated.props", bytes compile ]
