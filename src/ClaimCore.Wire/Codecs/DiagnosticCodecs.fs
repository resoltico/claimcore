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

    let private nullaryVariants<'T when 'T: equality> prefix (fields: 'T -> Fields<unit, unit>) =
        FSharpType.GetUnionCases typeof<'T>
        |> Array.filter (fun case -> case.GetFields().Length = 0)
        |> Array.map (fun case ->
            let value = (FSharpValue.MakeUnion(case, [||]) |> nonNull) :?> 'T

            variant
                (prefix + codeName case.Name)
                (fun () -> value)
                (fun candidate -> if candidate = value then Some() else None)
                (fields value))
        |> Array.toList

    let private count minimum =
        integer minimum (int64 Int32.MaxValue) |> map int int64

    let private violationVariants =
        nullaryVariants<Violation> "" (fun _ -> record ())
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
        Codec.fixedField "action" (RecommendedAction.token action)

    /// Every admitted scalar can contribute an issue; bounded row inputs add their own issues and a collection-level issue.
    let MaximumInputIssues =
        TaskId.all
        |> List.choose (function
            | TaskId.Operation _ as task -> Some(TaskDeclarations.describe task)
            | TaskId.Query _
            | TaskId.Command _ -> None)
        |> List.map (fun task ->
            task.Inputs.Length
            + (task.Collections
               |> List.sumBy (fun collection ->
                   1
                   + TaskCollectionId.maximumRows collection.Collection * collection.Inputs.Length)))
        |> List.max

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
            record (fun target related row -> target, related, row)
            |> field
                "field"
                (fun (field, _, _) -> field)
                (VocabularyCodec.create InputTarget.vocabulary |> named "InputTarget")
            |> optionalField "relatedDate" (fun (_, related, _) -> related) relatedDate
            |> optionalField
                "grantOrdinal"
                (fun (_, _, row) -> row)
                (integer 0L (int64 PeopleAdmission.MaximumGrants - 1L)
                 |> map (int >> GrantOrdinal.create >> Option.get) (GrantOrdinal.value >> int64))

        violationVariants
        |> List.map (
            extendVariant
                (fun (target, related, row) violation ->
                    {
                        Target = target
                        Violation = violation
                        GrantRow = row
                        RelatedDate = related
                    })
                (fun issue ->
                    Some((issue.Target, issue.RelatedDate, issue.GrantRow), issue.Violation))
                fields
        )

    let inputIssue = groupedTaggedOn "code" "InputIssue" issueVariants

    let private inputIssues =
        boundedList 1 MaximumInputIssues inputIssue
        |> map (InputIssues.ofList >> Option.get) InputIssues.toList

    let private nullaryRefusalFields refusal =
        let fields = record () |> actionField (Refusals.action refusal)

        match refusal with
        | Refusal.InvalidCursor ->
            fields |> Codec.fixedField "target" (InputTarget.token InputTarget.Cursor)
        | _ -> fields

    let private familyVariants<'T when 'T: equality> prefix (wrap: 'T -> Refusal) unwrap =
        nullaryVariants<'T> prefix (wrap >> nullaryRefusalFields)
        |> List.map (
            extendVariant
                (fun () value -> wrap value)
                (fun refusal -> unwrap refusal |> Option.map (fun value -> (), value))
                (record ())
        )

    let private caseVariants =
        (familyVariants "" Refusal.Case (function
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

    let private peopleVariants =
        familyVariants "" Refusal.People (function
            | Refusal.People value -> Some value
            | _ -> None)

    let private accessVariants =
        (familyVariants "" Refusal.Access (function
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
        ((familyVariants "REMINDER_" Refusal.Reminder (function
            | Refusal.Reminder value -> Some value
            | _ -> None))
         @ [
             variant
                 "REMINDER_NOT_OFFERED"
                 (ReminderRuleRefusal.NotOffered >> Refusal.Reminder)
                 (function
                 | Refusal.Reminder(ReminderRuleRefusal.NotOffered state) -> Some state
                 | _ -> None)
                 (record id
                  |> actionField RecommendedAction.ReviewLatest
                  |> field "state" id (VocabularyCodec.create ReminderState.vocabulary))
         ])

    let private conflictChanges =
        let page =
            (record (fun basis disclosure entries next ->
                {
                    ChangePage.Basis = basis
                    Disclosure = disclosure
                    Entries = entries
                    NextCursor = next
                })
            : Fields<ChangePage, _>)
            |> field "basis" _.Basis ResponseScalars.workDateBasis
            |> field "disclosure" _.Disclosure TimelineCodecs.disclosure
            |> field
                "entries"
                _.Entries
                (list RevisionConflictInfo.MaximumChanges TimelineCodecs.timelineEntry)
            |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
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
                "OPERATION_CLOCK_UNAVAILABLE"
                (fun (cutoff, observed) -> Refusal.OperationClockUnavailable(cutoff, observed))
                (function
                | Refusal.OperationClockUnavailable(cutoff, observed) -> Some(cutoff, observed)
                | _ -> None)
                (record (fun cutoff observed -> cutoff, observed)
                 |> actionField RecommendedAction.StopAndInvestigate
                 |> Codec.fixedField "phase" "NOT_STARTED"
                 |> field
                     "expiredBefore"
                     fst
                     ClaimCore.RecordFormat.CheckpointRecord.frontierInstant
                 |> field "observedAt" snd instant)
            variant
                "OPERATION_ID_OUT_OF_WINDOW"
                (fun (issuedAt, observedAt) -> Refusal.OperationIdOutOfWindow(issuedAt, observedAt))
                (function
                | Refusal.OperationIdOutOfWindow(issuedAt, observedAt) -> Some(issuedAt, observedAt)
                | _ -> None)
                (record (fun issuedAt observedAt -> issuedAt, observedAt)
                 |> actionField RecommendedAction.CorrectInput
                 |> Codec.fixedField "target" (InputTarget.token InputTarget.OperationId)
                 |> Codec.fixedField "phase" "NOT_STARTED"
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
        @ (nullaryVariants<Refusal> "" nullaryRefusalFields
           |> List.filter (fun variant -> variant.Tag <> "INVALID_CURSOR"))
        @ accessVariants
        @ peopleVariants
        @ (nullaryVariants<Refusal> "" nullaryRefusalFields
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
