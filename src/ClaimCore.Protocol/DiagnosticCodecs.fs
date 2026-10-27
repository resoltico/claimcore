namespace ClaimCore.Protocol

open System
open Microsoft.FSharp.Reflection
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json

/// Tagged cases own decoding, encoding, diagnostic arguments and catalog definitions together.
module DiagnosticCodecs =
    open Codec

    let private codeName = Vocabulary.formatToken TokenCasing.ScreamingSnake None

    let private nullaryVariants<'T when 'T: equality> (fields: 'T -> Fields<unit, unit>) =
        FSharpType.GetUnionCases typeof<'T>
        |> Array.filter (fun case -> case.GetFields().Length = 0)
        |> Array.map (fun case ->
            let value = (FSharpValue.MakeUnion(case, [||]) |> nonNull) :?> 'T

            variant
                (codeName case.Name)
                (fun () -> value)
                (fun candidate -> if candidate = value then Some() else None)
                (fields value))
        |> Array.toList

    let private count minimum =
        integer minimum (int64 Int32.MaxValue) |> map int int64

    let private violationVariants =
        nullaryVariants<Violation>(fun _ -> record ())
        @ [
            variant
                (codeName (nameof Violation.TooLong))
                Violation.TooLong
                (function
                | Violation.TooLong value -> Some value
                | _ -> None)
                (record id |> field "maximumCharacters" id (count 0L))
            variant
                (codeName (nameof Violation.DecimalFormat))
                Violation.DecimalFormat
                (function
                | Violation.DecimalFormat(integer, fraction) -> Some(integer, fraction)
                | _ -> None)
                (record (fun integer fraction -> integer, fraction)
                 |> field "maximumIntegerDigits" fst (count 1L)
                 |> field "maximumFractionalDigits" snd (count 0L))
            variant
                (codeName (nameof Violation.PageLimitRange))
                Violation.PageLimitRange
                (function
                | Violation.PageLimitRange value -> Some value
                | _ -> None)
                (record id |> field "maximumPageSize" id (count 1L))
        ]

    let violation = taggedOn "code" "Violation" violationVariants

    let private actionField action =
        ResponseScalars.fixedField "action" (RecommendedAction.token action)

    let private nullaryRefusalFields refusal =
        let fields = record () |> actionField (Refusals.action refusal)

        match Refusals.target refusal with
        | None -> fields
        | Some target -> fields |> ResponseScalars.fixedField "target" (InputTarget.token target)

    let private diagnosticVariants =
        let inputFields =
            record id
            |> actionField RecommendedAction.CorrectInput
            |> field
                "target"
                id
                (VocabularyCodec.create InputTarget.vocabulary |> named "InputTarget")

        (violationVariants
         |> List.map (
             extendVariant
                 (fun target violation -> Refusal.InvalidInput(target, violation))
                 (function
                 | Refusal.InvalidInput(target, violation) -> Some(target, violation)
                 | _ -> None)
                 inputFields
         ))
        @ [
            variant
                (codeName (nameof Refusal.RevisionConflict))
                Refusal.RevisionConflict
                (function
                | Refusal.RevisionConflict revision -> Some revision
                | _ -> None)
                (record id
                 |> actionField RecommendedAction.ReadCurrent
                 |> field "actualRevision" id ResponseScalars.revision)
        ]
        @ nullaryVariants<Refusal> nullaryRefusalFields

    let diagnostic = groupedTaggedOn "code" "Diagnostic" diagnosticVariants

    let private definitionsOf (variants: Variant<'T> list) : DiagnosticDefinition list =
        variants
        |> List.map (fun variant ->
            let parameters =
                match variant.VariantSchema with
                | Schema.Object(properties, _) ->
                    properties
                    |> List.choose (function
                        | name, Schema.Integer(minimum, maximum) when name <> "actualRevision" ->
                            Some
                                {
                                    Name = name
                                    Minimum = int minimum
                                    Maximum = int maximum
                                }
                        | _ -> None)
                | _ -> invalidOp "Diagnostic alternatives require object fields."

            {
                Id = variant.Tag
                Parameters = parameters
            })

    let definitions = definitionsOf diagnosticVariants
    let codes = definitions |> List.map _.Id

    /// Presentation consumes the codec's fields; no parallel code or parameter table is maintained.
    let describe refusal =
        match diagnostic.Write refusal with
        | JsonValue.Object(("code", JsonValue.String code) :: fields) ->
            let parameters =
                fields
                |> List.choose (function
                    | name, JsonValue.Integer value when name <> "actualRevision" ->
                        Some(name, int value)
                    | _ -> None)

            code, parameters
        | _ -> invalidOp "A diagnostic codec must write its code and typed fields."

    let code refusal = describe refusal |> fst
    let parameters refusal = describe refusal |> snd

    let private faultVariants =
        CoreFault.all
        |> List.map (fun (value, code) ->
            variant
                code
                (fun () -> value)
                (fun candidate -> if candidate = value then Some() else None)
                (record () |> actionField value.Action))

    let fault = groupedTaggedOn "code" "Fault" faultVariants
    let faultDefinitions = definitionsOf faultVariants
