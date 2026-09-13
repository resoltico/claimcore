namespace ClaimCore.Docs

open System
open System.IO
open System.Text
open System.Text.Json

type ConvergenceSource =
    {
        Id: string
        Kind: string
        Inventory: string
        Tests: string list
    }

[<RequireQualifiedAccess>]
module ConvergenceInventory =
    let private dotnetSource assembly =
        {
            Id = "dotnet:" + assembly
            Kind = "dotnet-mtp"
            Inventory = "eng/ClaimCore.Docs/test-inventory/" + assembly + ".json"
            Tests = TestInventory.names assembly |> Set.toList
        }

    let sources () =
        let dotnet = TestInventory.assemblies |> List.map dotnetSource

        let frontend =
            [
                {
                    Id = "frontend:playwright"
                    Kind = "playwright"
                    Inventory = "eng/ClaimCore.Docs/FrontendTestCatalog.fs"
                    Tests = FrontendTestCatalog.browser |> Set.toList
                }
                {
                    Id = "frontend:vitest"
                    Kind = "vitest"
                    Inventory = "eng/ClaimCore.Docs/FrontendTestCatalog.fs"
                    Tests = FrontendTestCatalog.vitest |> Set.toList
                }
            ]

        dotnet @ frontend |> List.sortBy _.Id

    let identities () =
        sources ()
        |> List.collect (fun source ->
            source.Tests |> List.map (fun name -> source.Id + "::" + name))

    let private writeStrings (writer: Utf8JsonWriter) (values: string list) =
        writer.WriteStartArray()
        values |> List.iter (fun value -> writer.WriteStringValue(value: string))
        writer.WriteEndArray()

    let render () =
        let sources = sources ()

        let count kind =
            sources
            |> List.filter (fun source -> source.Kind = kind)
            |> List.sumBy _.Tests.Length

        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WritePropertyName("sources")
        writer.WriteStartArray()

        sources
        |> List.iter (fun source ->
            writer.WriteStartObject()
            writer.WriteString("id", source.Id)
            writer.WriteString("kind", source.Kind)
            writer.WriteString("inventory", source.Inventory)
            writer.WritePropertyName("tests")
            writeStrings writer source.Tests
            writer.WriteEndObject())

        writer.WriteEndArray()
        writer.WritePropertyName("counts")
        writer.WriteStartObject()
        writer.WriteNumber("dotnetMtp", count "dotnet-mtp")
        writer.WriteNumber("frontendUnit", count "vitest")
        writer.WriteNumber("browser", count "playwright")
        writer.WriteNumber("total", sources |> List.sumBy _.Tests.Length)
        writer.WriteEndObject()
        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray()) + "\n"
