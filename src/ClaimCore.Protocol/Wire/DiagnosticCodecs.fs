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

    let MaximumInputIssues =
        let definitions = RequestCodecs.operationInput.Definitions

        let width subject family token =
            let selection = Map.ofList [ RequestSchemas.OperationInput, subject; family, token ]

            Bindings.walk definitions selection RequestCodecs.operationInput.Schema
            |> List.choose (function
                | BindingNode.Input input -> Some input
                | _ -> None)
            |> List.length

        let caseWidths =
            CaseActKind.all
            |> List.map (fun kind ->
                width
                    (ActSubject.token ActSubject.Case)
                    RequestSchemas.CaseAct
                    (CaseActKind.token kind))

        let authorityWidths =
            AuthorityKind.all
            |> List.filter ((<>) AuthorityKind.ProvisionOwner)
            |> List.map (fun kind ->
                width
                    (ActSubject.token ActSubject.Authority)
                    RequestSchemas.AuthorityAct
                    (AuthorityKind.token kind))

        List.max (caseWidths @ authorityWidths)

    let private issueVariants =
        let fields =
            record id
            |> field
                "field"
                id
                (VocabularyCodec.create InputTarget.vocabulary |> named "InputTarget")

        violationVariants
        |> List.map (
            extendVariant
                (fun target violation ->
                    {
                        Target = target
                        Violation = violation
                    })
                (fun issue -> Some(issue.Target, issue.Violation))
                fields
        )

    let inputIssue = groupedTaggedOn "code" "InputIssue" issueVariants

    let private inputIssues =
        list MaximumInputIssues inputIssue
        |> constrained
            (Schema.Array(inputIssue.Schema, 1, MaximumInputIssues))
            (List.isEmpty >> not)
        |> map (InputIssues.ofList >> Option.get) InputIssues.toList

    let private nullaryRefusalFields refusal =
        let fields = record () |> actionField (Refusals.action refusal)

        match refusal with
        | Refusal.InvalidCursor ->
            fields
            |> ResponseScalars.fixedField "target" (InputTarget.token InputTarget.Cursor)
        | Refusal.OperationResolved ->
            fields
            |> ResponseScalars.fixedField "target" (InputTarget.token InputTarget.OperationId)
        | _ -> fields

    let private familyVariants<'T when 'T: equality> (wrap: 'T -> Refusal) unwrap =
        nullaryVariants<'T>(wrap >> nullaryRefusalFields)
        |> List.map (
            extendVariant
                (fun () value -> wrap value)
                (fun refusal -> unwrap refusal |> Option.map (fun value -> (), value))
                (record ())
        )

    let private caseVariants =
        familyVariants Refusal.Case (function
            | Refusal.Case value -> Some value
            | _ -> None)

    let private authorityVariants =
        familyVariants Refusal.Authority (function
            | Refusal.Authority value -> Some value
            | _ -> None)

    let private accessVariants =
        familyVariants Refusal.Access (function
            | Refusal.Access value -> Some value
            | _ -> None)

    let private diagnosticVariants =
        [
            variant
                "INVALID_INPUT"
                Refusal.InvalidInput
                (function
                | Refusal.InvalidInput issues -> Some issues
                | _ -> None)
                (record id
                 |> actionField RecommendedAction.CorrectInput
                 |> field "issues" id inputIssues)
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
        @ caseVariants
        @ (nullaryVariants<Refusal> nullaryRefusalFields
           |> List.filter (fun variant -> variant.Tag <> "INVALID_CURSOR"))
        @ accessVariants
        @ authorityVariants
        @ (nullaryVariants<Refusal> nullaryRefusalFields
           |> List.filter (fun variant -> variant.Tag = "INVALID_CURSOR"))

    let diagnostic = groupedTaggedOn "code" "Diagnostic" diagnosticVariants

    let private definitionsOf (variants: Variant<'T> list) : DiagnosticDefinition list =
        variants
        |> List.map (fun variant ->
            let parameters =
                match variant.VariantSchema with
                | Schema.Object(properties, _) ->
                    properties
                    |> List.choose (function
                        | "issues", Schema.Array(_, minimum, maximum) ->
                            Some
                                {
                                    Name = "issueCount"
                                    Minimum = minimum
                                    Maximum = maximum
                                }
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

    let violationDefinitions = definitionsOf violationVariants
    let definitions = definitionsOf diagnosticVariants
    let codes = definitions |> List.map _.Id

    /// Presentation consumes the codec's fields; no parallel code or parameter table is maintained.
    let private described =
        function
        | JsonValue.Object(("code", JsonValue.String code) :: fields) ->
            let parameters =
                fields
                |> List.choose (function
                    | name, JsonValue.Integer value when name <> "actualRevision" ->
                        Some(name, int value)
                    | _ -> None)

            code, parameters
        | _ -> invalidOp "A diagnostic codec must write its code and typed fields."

    let describeIssue issue = inputIssue.Write issue |> described

    let describe refusal =
        match refusal with
        | Refusal.InvalidInput issues ->
            "INVALID_INPUT", [ "issueCount", InputIssues.toList issues |> List.length ]
        | refusal -> diagnostic.Write refusal |> described

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
