namespace ClaimCore.Wire

open System
open Microsoft.FSharp.Reflection
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
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
                (codeName (nameof Violation.GrantCountRange))
                Violation.GrantCountRange
                (function
                | Violation.GrantCountRange value -> Some value
                | _ -> None)
                (record id |> field "maximumGrants" id (count 1L))
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
            // Reference selectors carry authored scalar validation; typed IDs cannot add an independent business issue.
            let selection =
                Map.ofList
                    [
                        RequestSchemas.OperationInput, subject
                        family, token
                        RequestSchemas.CaseSelector, "REFERENCE"
                    ]

            Bindings.walk definitions selection RequestCodecs.operationInput.Schema
            |> Bindings.inputWidth

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

    let private relatedDate =
        (record (fun field value ->
            {
                RelatedDate.Field = field
                Value = value
            })
        : Fields<RelatedDate, _>)
        |> field "field" _.Field (VocabularyCodec.create BusinessField.vocabulary)
        |> field
            "value"
            _.Value
            (ResponseScalars.date
             |> map
                 (CalendarDate.parse >> Result.defaultWith (fun _ -> invalidOp "Admitted date"))
                 CalendarDate.format)
        |> build
        |> named "RelatedDate"

    let private issueVariants =
        let fields =
            record (fun target related -> target, related)
            |> field
                "field"
                fst
                (VocabularyCodec.create InputTarget.vocabulary |> named "InputTarget")
            |> optionalField "relatedDate" snd relatedDate

        violationVariants
        |> List.map (
            extendVariant
                (fun (target, related) violation ->
                    {
                        Target = target
                        Violation = violation
                        RelatedDate = related
                    })
                (fun issue -> Some((issue.Target, issue.RelatedDate), issue.Violation))
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
        (familyVariants Refusal.Case (function
            | Refusal.Case value -> Some value
            | _ -> None))
        @ [
            variant
                "NOT_OFFERED"
                (CaseRefusal.NotOffered >> Refusal.Case)
                (function
                | Refusal.Case(CaseRefusal.NotOffered state) -> Some state
                | _ -> None)
                (record id
                 |> actionField RecommendedAction.ReviewLatest
                 |> field "state" id (VocabularyCodec.create CaseHeadline.vocabulary))
        ]

    let private authorityVariants =
        familyVariants Refusal.Authority (function
            | Refusal.Authority value -> Some value
            | _ -> None)

    let private accessVariants =
        (familyVariants Refusal.Access (function
            | Refusal.Access value -> Some value
            | _ -> None))
        @ [
            variant
                "NOT_AUTHORIZED"
                (AccessRefusal.NotAuthorized >> Refusal.Access)
                (function
                | Refusal.Access(AccessRefusal.NotAuthorized required) -> Some required
                | _ -> None)
                (record id
                 |> actionField RecommendedAction.AskForAccess
                 |> field "required" id (VocabularyCodec.create Capability.vocabulary))
        ]

    let private reminderVariants =
        ((familyVariants Refusal.Reminder (function
            | Refusal.Reminder value -> Some value
            | _ -> None))
         @ [
             variant
                 "NOT_OFFERED"
                 (ReminderRuleRefusal.NotOffered >> Refusal.Reminder)
                 (function
                 | Refusal.Reminder(ReminderRuleRefusal.NotOffered state) -> Some state
                 | _ -> None)
                 (record id
                  |> actionField RecommendedAction.ReviewLatest
                  |> field "state" id (VocabularyCodec.create ReminderState.vocabulary))
         ])
        |> List.map (fun variant ->
            { variant with
                Tag = "REMINDER_" + variant.Tag
            })

    let private conflictChanges =
        let page =
            (record (fun entries next _view ->
                {
                    ChangePage.Entries = entries
                    NextCursor = next
                })
            : Fields<ChangePage, _>)
            |> field
                "entries"
                _.Entries
                (list RevisionConflictInfo.MaximumChanges TimelineCodecs.timelineEntry)
            |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
            |> field
                "view"
                (fun page -> Descriptor.timelineView page.Entries)
                PresentationCodecs.view
            |> build
            |> named "ConflictChangePage"

        tagged
            "ConflictChanges"
            [
                variant
                    "PAGE"
                    ConflictChanges.Page
                    (function
                    | ConflictChanges.Page value -> Some value
                    | _ -> None)
                    (record id |> field "page" id page)
                unitVariant "WITHHELD" ConflictChanges.Withheld
            ]

    let private revisionConflict =
        (record (fun revision changes ->
            RevisionConflictInfo.at revision |> RevisionConflictInfo.explain changes)
        : Fields<RevisionConflictInfo, _>)
        |> actionField RecommendedAction.ReviewLatest
        |> field "actualRevision" _.ActualRevision ResponseScalars.revision
        |> field
            "changes"
            (fun conflict ->
                conflict.Changes
                |> Option.defaultWith (fun () ->
                    invalidOp "A revision conflict requires its authorized snapshot explanation."))
            conflictChanges

    let private diagnosticVariants =
        [
            variant
                "OPERATION_ID_OUT_OF_WINDOW"
                (fun (issuedAt, observedAt) -> Refusal.OperationIdOutOfWindow(issuedAt, observedAt))
                (function
                | Refusal.OperationIdOutOfWindow(issuedAt, observedAt) -> Some(issuedAt, observedAt)
                | _ -> None)
                (record (fun issuedAt observedAt -> issuedAt, observedAt)
                 |> actionField RecommendedAction.CorrectInput
                 |> ResponseScalars.fixedField "target" (InputTarget.token InputTarget.OperationId)
                 |> ResponseScalars.fixedField "phase" "NOT_STARTED"
                 |> field "issuedAt" fst instant
                 |> field "observedAt" snd instant)
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
                | Refusal.RevisionConflict conflict -> Some conflict
                | _ -> None)
                revisionConflict
        ]
        @ caseVariants
        @ reminderVariants
        @ (nullaryVariants<Refusal> nullaryRefusalFields
           |> List.filter (fun variant -> variant.Tag <> "INVALID_CURSOR"))
        @ accessVariants
        @ authorityVariants
        @ (nullaryVariants<Refusal> nullaryRefusalFields
           |> List.filter (fun variant -> variant.Tag = "INVALID_CURSOR"))

    let diagnostic = groupedTaggedOn "code" "Diagnostic" diagnosticVariants

    let caseActAvailability =
        diagnosticVariants
        |> List.filter (fun variant ->
            List.contains
                variant.Tag
                [
                    "CASE_ALREADY_EXISTS"
                    "NOT_OFFERED"
                    "RESOURCE_UNAVAILABLE"
                    "NOT_AUTHORIZED"
                    "REGISTER_MAINTENANCE"
                ])
        |> groupedTaggedOn "code" "CaseActAvailabilityReason"

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
                                    Rule = DiagnosticParameterRule.IntegerRange(minimum, maximum)
                                }
                        | name, Schema.Integer(minimum, maximum) when name <> "actualRevision" ->
                            Some
                                {
                                    Name = name
                                    Rule =
                                        DiagnosticParameterRule.IntegerRange(
                                            int minimum,
                                            int maximum
                                        )
                                }
                        | name, Schema.Vocabulary(vocabulary, Schema.OneOf(values, _)) ->
                            Schema.tryLiteralSet values
                            |> Option.map (fun values ->
                                {
                                    Name = name
                                    Rule = DiagnosticParameterRule.Vocabulary(vocabulary, values)
                                })
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
            let definition =
                definitions @ violationDefinitions
                |> List.find (fun definition -> definition.Id = code)

            let names = definition.Parameters |> List.map _.Name |> Set.ofList
            code, fields |> List.filter (fun (name, _) -> Set.contains name names)
        | _ -> invalidOp "A diagnostic codec must write its code and typed fields."

    let describeIssue issue = inputIssue.Write issue |> described

    let describe refusal =
        match refusal with
        | Refusal.InvalidInput issues ->
            "INVALID_INPUT",
            [
                "issueCount", JsonValue.Integer(int64 (InputIssues.toList issues |> List.length))
            ]
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
