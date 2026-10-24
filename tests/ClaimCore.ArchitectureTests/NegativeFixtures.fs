namespace ClaimCore.ArchitectureTests.NegativeFixtures

open ClaimCore.Contract

open System

type PostgresReference =
    static member Target() = typeof<ClaimCore.Postgres.IRuntime>

type ClockCall =
    static member Read() = DateTimeOffset.UtcNow

type FileCall =
    static member Read(path) = IO.File.ReadAllText(path)

type EnvironmentCall =
    static member Read() =
        Environment.GetEnvironmentVariable("ARCHITECTURE_NEGATIVE_CONTROL")

type RandomCall =
    static member Read() = Guid.NewGuid()

type NetworkCall =
    static member Read() = new Net.Http.HttpClient()

type TimeZoneCall =
    static member Read() =
        TimeZoneInfo.FindSystemTimeZoneById("Etc/UTC")

type JsonSerializerCall =
    static member Write() =
        System.Text.Json.JsonSerializer.Serialize("control")

type JsonWriterCall =
    static member Write() =
        let buffer = System.Buffers.ArrayBufferWriter<byte>()
        use writer = new System.Text.Json.Utf8JsonWriter(buffer)
        writer.WriteNullValue()
        writer.Flush()

type ResourceCall =
    static member Read() =
        Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("synthetic")

type FileReaderCall =
    static member Read() =
        use reader = new IO.StreamReader("synthetic")
        reader.ReadToEnd()
