namespace ClaimCore.Tests.Architecture.NegativeFixtures

open ClaimCore.Domain
open ClaimCore.Protocol

open System
open System.Threading.Tasks

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

/// A compiled wire-codec dependency must never enter a durable grammar.
type WireCodecCall =
    static member Read() =
        ClaimCore.Protocol.RequestCodecs.caseRequest.Schema

/// Distinct names let controls select compiler-generated bodies as well as declaring types.
type AmbientShapes =
    static member AmbientDirect() = DateTimeOffset.UtcNow

type AmbientTaskShape =
    static member AmbientTask() =
        task {
            do! Task.Yield()
            return DateTimeOffset.UtcNow
        }

type AmbientAsyncShape =
    static member AmbientAsync() =
        async {
            do! Async.Sleep 0
            return DateTimeOffset.UtcNow
        }

type AmbientClosureShape =
    static member AmbientClosure() =
        List.map (fun (_: int) -> DateTimeOffset.UtcNow) [ 1 ]

type AmbientTaskClosureShape =
    static member AmbientTaskClosure() =
        List.map (fun (_: int) -> task { return DateTimeOffset.UtcNow }) [ 1 ]

type PostgresShapes =
    static member PostgresDirect() = typeof<ClaimCore.Postgres.IRuntime>

type PostgresTaskShape =
    static member PostgresTask() =
        task {
            do! Task.Yield()
            return typeof<ClaimCore.Postgres.IRuntime>
        }

type DurableShapes =
    static member DurableDirect() = ReceiptCodecs.receipt.Schema

type DurableTaskShape =
    static member DurableTask() =
        task {
            do! Task.Yield()
            return ReceiptCodecs.receipt.Schema
        }

type SerializerShapes =
    static member SerializerDirect() =
        System.Text.Json.JsonSerializer.Serialize("control")

type SerializerTaskShape =
    static member SerializerTask() =
        task {
            do! Task.Yield()
            return System.Text.Json.JsonSerializer.Serialize("control")
        }

type ApplicationShapes =
    static member ApplicationDirect() = ReplyCodecs.execute.Schema

type ApplicationTaskShape =
    static member ApplicationTask() =
        task {
            do! Task.Yield()
            return ReplyCodecs.execute.Schema
        }

type DomainShapes =
    static member DomainDirect() =
        typeof<ClaimCore.RecordFormat.DecodeProblem>

type DomainTaskShape =
    static member DomainTask() =
        task {
            do! Task.Yield()
            return typeof<ClaimCore.RecordFormat.DecodeProblem>
        }

[<RequireQualifiedAccess>]
type VocabularySample = Sample

module MisnamedVocabulary =
    let vocabulary =
        Vocabulary.ofUnion<VocabularySample> "sample" TokenCasing.CamelCase None

    let Direct () = Vocabulary.all vocabulary

module MisnamedTaskVocabulary =
    let vocabulary =
        Vocabulary.ofUnion<VocabularySample> "sample" TokenCasing.CamelCase None

    let TaskShape () =
        task {
            do! Task.Yield()
            return Vocabulary.all vocabulary
        }

type JsonFromDomain =
    static member JsonDirect() = ClaimCore.Json.JsonValue.Null

type JsonFromDomainTask =
    static member JsonTask() =
        task {
            do! Task.Yield()
            return ClaimCore.Json.JsonValue.Null
        }
