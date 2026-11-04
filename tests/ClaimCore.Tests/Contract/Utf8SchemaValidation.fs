module internal ClaimCore.Tests.Utf8SchemaValidation

open System
open System.Text
open System.Text.Json
open global.Json.Schema

// This independent engine implementation intentionally does not call ClaimCore's codecs or byte predicate.
type private MaximumUtf8BytesKeyword() =
    interface IKeywordHandler with
        member _.Name = "x-claimcore-maxUtf8Bytes"

        member _.ValidateKeywordValue value =
            match value.TryGetInt32() with
            | true, maximum when maximum >= 0 -> box maximum
            | _ -> invalidOp "The UTF-8 byte-limit keyword needs a nonnegative Int32."

        member _.BuildSubschemas(_, _) = ()

        member _.Evaluate(keyword, context) =
            let valid =
                context.Instance.ValueKind <> JsonValueKind.String
                || Encoding.UTF8.GetByteCount(context.Instance.GetString() |> nonNull)
                   <= unbox<int> keyword.Value

            KeywordEvaluation(
                Keyword = "x-claimcore-maxUtf8Bytes",
                IsValid = valid,
                ContributesToValidation = true,
                Error =
                    if valid then
                        ""
                    else
                        "String exceeds its declared UTF-8 byte limit."
            )

let private dialect =
    Dialect.Draft202012.With(
        [ MaximumUtf8BytesKeyword() :> IKeywordHandler ],
        id = Dialect.Draft202012.Id
    )

let private registry =
    let registry = DialectRegistry()
    registry.Register dialect
    registry

/// The local registry makes explicit $schema declarations select the extension-aware wire profile too.
/// No global validator registry is changed, and no instance is rewritten.
let build source =
    JsonSchema.FromText(source, BuildOptions(Dialect = dialect, DialectRegistry = registry))

let evaluate (schema: JsonSchema) (value: JsonElement) =
    schema.Evaluate(value, EvaluationOptions(RequireFormatValidation = true)).IsValid
