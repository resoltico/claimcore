namespace ClaimCore.RecordFormat

open ClaimCore.Json

open System
open System.Globalization
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type CaseListPosition =
    | Reference of string
    | Recent of changedAt: DateTimeOffset * reference: string
    | Notified of notifiedOn: CalendarDate option * reference: string
    | FollowUp of followUp: CalendarDate option * reference: string

module CaseListPosition =
    let reference =
        function
        | CaseListPosition.Reference reference
        | CaseListPosition.Recent(_, reference)
        | CaseListPosition.Notified(_, reference)
        | CaseListPosition.FollowUp(_, reference) -> reference

    let create order changedAt notifiedOn followUp reference =
        match order with
        | CaseListOrder.Reference -> CaseListPosition.Reference(CaseReference.key reference)
        | CaseListOrder.Recent -> CaseListPosition.Recent(changedAt, CaseReference.key reference)
        | CaseListOrder.Notified ->
            CaseListPosition.Notified(notifiedOn, CaseReference.key reference)
        | CaseListOrder.FollowUp -> CaseListPosition.FollowUp(followUp, CaseReference.key reference)

type GrantPosition = { Role: Role; CaseId: CaseId option }

type TimelinePosition =
    {
        BeforeSequence: Sequence
        Range: ChangeRange option
    }

/// Plain positions convey no authority. Every selection authorizes before decoding.
type AccessRequestPosition =
    {
        LastSeen: DateTimeOffset
        Principal: PrincipalKey
    }

module Cursors =
    [<Literal>]
    let MaximumTokenCharacters = 512

    let private base64 (bytes: byte array) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private encode (installation: Guid) members =
        JsonValue.Object(
            ("installationId", JsonValue.String(installation.ToString("D"))) :: members
        )
        |> CanonicalWriter.bytes
        |> base64

    let private alphabet character =
        (character >= 'A' && character <= 'Z')
        || (character >= 'a' && character <= 'z')
        || (character >= '0' && character <= '9')
        || character = '-'
        || character = '_'

    let private decode (installation: Guid) maximum read (token: string) =
        if
            String.IsNullOrEmpty token
            || token.Length > maximum
            || token.Length % 4 = 1
            || not (Seq.forall alphabet token)
        then
            Error Refusal.InvalidCursor
        else
            try
                let padded =
                    token.Replace('-', '+').Replace('_', '/')
                    + String('=', (4 - token.Length % 4) % 4)

                let bytes = Convert.FromBase64String padded

                match StrictJson.read maximum bytes with
                | Error _ -> Error Refusal.InvalidCursor
                | Ok value ->
                    if base64 bytes <> token || CanonicalWriter.bytes value <> bytes then
                        Error Refusal.InvalidCursor
                    else
                        let scoped =
                            match value with
                            | JsonValue.Object members when
                                JsonValue.tryMember "installationId" value =
                                    Some(JsonValue.String(installation.ToString("D")))
                                ->
                                read (
                                    JsonValue.Object(
                                        members |> List.filter (fst >> (<>) "installationId")
                                    )
                                )
                            | _ -> None

                        match scoped with
                        | Some position -> Ok position
                        | None -> Error Refusal.InvalidCursor
            with
            | :? ArgumentException
            | :? FormatException
            | :? InvalidOperationException -> Error Refusal.InvalidCursor

    let private reference value =
        (CaseReference.parse
         >> Result.map ignore
         >> Result.mapError (fun violation ->
             Refusal.invalidInput (InputTarget.Field BusinessField.CaseReference, violation)))
            value
        |> Result.isOk
        && CaseReference.key value = value

    let private uuid create value =
        match Guid.TryParseExact((value: string), "D") with
        | true, id when id.ToString("D") = value -> create id
        | _ -> None

    let private caseFilters (filter: CaseListFilter<CaseSearchText>) =
        JsonValue.Object
            [
                "lifecycle",
                JsonValue.Array(filter.Lifecycles |> List.map (Lifecycle.token >> JsonValue.String))
                "status",
                JsonValue.Array(filter.Statuses |> List.map (CaseStatus.token >> JsonValue.String))
                "progress",
                JsonValue.Array(
                    filter.Progress |> List.map (CaseProgress.token >> JsonValue.String)
                )
                "text",
                filter.Text
                |> Option.map (CaseSearchText.value >> JsonValue.String)
                |> Option.defaultValue JsonValue.Null
                "order", JsonValue.String(CaseListOrder.token filter.Order)
                "due",
                filter.Due
                |> Option.map (CaseDue.token >> JsonValue.String)
                |> Option.defaultValue JsonValue.Null
                "assignment",
                filter.Assignment
                |> Option.map (CaseAssignment.token >> JsonValue.String)
                |> Option.defaultValue JsonValue.Null
                "responsibleActor",
                filter.ResponsibleActor
                |> Option.map (ActorId.format >> JsonValue.String)
                |> Option.defaultValue JsonValue.Null
                "view",
                filter.View
                |> Option.map (CaseView.token >> JsonValue.String)
                |> Option.defaultValue JsonValue.Null
            ]

    let private instant (value: string) =
        match
            DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None
            )
        with
        | true, parsed when
            parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) = value
            && RecordedInstant.hasMicrosecondPrecision parsed
            ->
            Some parsed
        | _ -> None

    let encodeCases installation (filter: CaseListFilter<CaseSearchText>) position =
        let after =
            match filter.Order, position with
            | CaseListOrder.Reference, CaseListPosition.Reference reference ->
                JsonValue.Object [ "reference", JsonValue.String(CaseReference.key reference) ]
            | CaseListOrder.Recent, CaseListPosition.Recent(changedAt, reference) ->
                if not (RecordedInstant.hasMicrosecondPrecision changedAt) then
                    invalidArg (nameof position) "Use microsecond precision."

                JsonValue.Object
                    [
                        "reference", JsonValue.String(CaseReference.key reference)
                        "changedAt",
                        JsonValue.String(
                            changedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                        )
                    ]
            | CaseListOrder.Notified, CaseListPosition.Notified(date, reference) ->
                JsonValue.Object
                    [
                        "notifiedOn",
                        date
                        |> Option.map (CalendarDate.format >> JsonValue.String)
                        |> Option.defaultValue JsonValue.Null
                        "reference", JsonValue.String(CaseReference.key reference)
                    ]
            | CaseListOrder.FollowUp, CaseListPosition.FollowUp(date, reference) ->
                JsonValue.Object
                    [
                        "followUpDate",
                        date
                        |> Option.map (CalendarDate.format >> JsonValue.String)
                        |> Option.defaultValue JsonValue.Null
                        "reference", JsonValue.String(CaseReference.key reference)
                    ]
            | _ -> invalidArg (nameof position) "The position must match the selected order."

        encode
            installation
            [
                "after", after
                "filters", caseFilters filter
                "kind", JsonValue.String "CASES"
                "v", JsonValue.Integer 6L
            ]

    let private casePosition order =
        function
        | JsonValue.Object [ "reference", JsonValue.String after ] when
            order = CaseListOrder.Reference && reference after
            ->
            Some(CaseListPosition.Reference after)
        | JsonValue.Object [ "changedAt", JsonValue.String changedAt
                             "reference", JsonValue.String after ] when
            order = CaseListOrder.Recent && reference after
            ->
            instant changedAt
            |> Option.map (fun time -> CaseListPosition.Recent(time, after))
        | JsonValue.Object [ "notifiedOn", date; "reference", JsonValue.String after ] when
            order = CaseListOrder.Notified && reference after
            ->
            match date with
            | JsonValue.Null -> Some(CaseListPosition.Notified(None, after))
            | JsonValue.String raw ->
                CalendarDate.parse raw
                |> Result.toOption
                |> Option.map (fun date -> CaseListPosition.Notified(Some date, after))
            | _ -> None
        | JsonValue.Object [ "followUpDate", date; "reference", JsonValue.String after ] when
            order = CaseListOrder.FollowUp && reference after
            ->
            match date with
            | JsonValue.Null -> Some(CaseListPosition.FollowUp(None, after))
            | JsonValue.String raw ->
                CalendarDate.parse raw
                |> Result.toOption
                |> Option.map (fun date -> CaseListPosition.FollowUp(Some date, after))
            | _ -> None
        | _ -> None

    let decodeCases installation (filter: CaseListFilter<CaseSearchText>) token =
        decode
            installation
            16384
            (function
            | JsonValue.Object [ "after", after
                                 "filters", filters
                                 "kind", JsonValue.String "CASES"
                                 "v", JsonValue.Integer 6L ] when
                CanonicalWriter.bytes filters = CanonicalWriter.bytes (caseFilters filter)
                ->
                casePosition filter.Order after
            | _ -> None)
            token

    let encodeAssignees installation caseId (after: ActorId) =
        encode
            installation
            [
                "kind", JsonValue.String "ASSIGNEES"
                "v", JsonValue.Integer 1L
                "caseId", JsonValue.String(CaseId.format caseId)
                "after", JsonValue.String(ActorId.format after)
            ]

    let decodeAssignees installation caseId token =
        decode
            installation
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ "after", JsonValue.String after
                                 "caseId", JsonValue.String target
                                 "kind", JsonValue.String "ASSIGNEES"
                                 "v", JsonValue.Integer 1L ] when
                uuid CaseId.create target = Some caseId
                ->
                uuid ActorId.create after
            | _ -> None)
            token

    let encodeAccessRequests installation (after: AccessRequestPosition) =
        if after.LastSeen.Ticks % 10L <> 0L then
            invalidArg (nameof after) "Access-request positions require database instant precision."

        let kind, issuer, subject = PrincipalKey.parts after.Principal

        encode
            installation
            [
                "kind", JsonValue.String "ACCESS_REQUESTS"
                "v", JsonValue.Integer 1L
                "lastSeen",
                JsonValue.String(
                    after.LastSeen.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                )
                "principalKind", JsonValue.String((Vocabulary.token PrincipalKind.vocabulary) kind)
                "issuer", JsonValue.String issuer
                "subject", JsonValue.String subject
            ]

    let decodeAccessRequests maximum installation token =
        decode
            installation
            maximum
            (function
            | JsonValue.Object [ "issuer", JsonValue.String issuer
                                 "kind", JsonValue.String "ACCESS_REQUESTS"
                                 "lastSeen", JsonValue.String seen
                                 "principalKind", JsonValue.String kind
                                 "subject", JsonValue.String subject
                                 "v", JsonValue.Integer 1L ] ->
                match
                    DateTimeOffset.TryParseExact(
                        seen,
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None
                    ),
                    (Vocabulary.tryParse PrincipalKind.vocabulary) kind
                with
                | (true, date), Some kind when date.Offset = TimeSpan.Zero && date.Ticks % 10L = 0L ->
                    PrincipalKey.ofKind kind issuer subject
                    |> Result.toOption
                    |> Option.map (fun principal ->
                        {
                            AccessRequestPosition.LastSeen = date
                            Principal = principal
                        })
                | _ -> None
            | _ -> None)
            token

    let private actorQueryFingerprint text =
        text
        |> Option.map (fun value ->
            System.Text.Encoding.UTF8.GetBytes(
                "claimcore/directory-text-query/1\n" + DirectorySearchText.value value
            )
            |> System.Security.Cryptography.SHA256.HashData
            |> Convert.ToHexStringLower
            |> JsonValue.String)
        |> Option.defaultValue JsonValue.Null

    let encodeActors installation text role (after: ActorId) =
        encode
            installation
            [
                "after", JsonValue.String(ActorId.format after)
                "kind", JsonValue.String "ACTORS"
                "query", actorQueryFingerprint text
                "role",
                (role
                 |> Option.map (Role.token >> JsonValue.String)
                 |> Option.defaultValue JsonValue.Null)
                "v", JsonValue.Integer 4L
            ]

    let decodeActors maximum installation text role token =
        let expected = actorQueryFingerprint text

        let expectedRole =
            role
            |> Option.map (Role.token >> JsonValue.String)
            |> Option.defaultValue JsonValue.Null

        decode
            installation
            maximum
            (function
            | JsonValue.Object [ "after", JsonValue.String after
                                 "kind", JsonValue.String "ACTORS"
                                 "query", actual
                                 "role", actualRole
                                 "v", JsonValue.Integer 4L ] when
                actual = expected && actualRole = expectedRole
                ->
                uuid ActorId.create after
            | _ -> None)
            token

    let encodeGrants installation (actorId: ActorId) (after: GrantPosition) =
        let position =
            JsonValue.Object
                [
                    "caseId",
                    after.CaseId
                    |> Option.map (fun id -> JsonValue.String(CaseId.format id))
                    |> Option.defaultValue JsonValue.Null
                    "role", JsonValue.String(Role.token after.Role)
                ]

        encode
            installation
            [
                "actor", JsonValue.String(ActorId.format actorId)
                "after", position
                "kind", JsonValue.String "GRANTS"
                "v", JsonValue.Integer 2L
            ]

    let private grant role caseId =
        let scope =
            match caseId with
            | JsonValue.Null -> Some None
            | JsonValue.String value -> uuid CaseId.create value |> Option.map Some
            | _ -> None

        match (Vocabulary.tryParse Role.vocabulary) role, scope with
        | Some role, Some caseId -> Some { Role = role; CaseId = caseId }
        | _ -> None

    let decodeGrants installation actorId token =
        decode
            installation
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ "actor", JsonValue.String target
                                 "after",
                                 JsonValue.Object [ "caseId", caseId; "role", JsonValue.String role ]
                                 "kind", JsonValue.String "GRANTS"
                                 "v", JsonValue.Integer 2L ] when
                uuid ActorId.create target = Some actorId
                ->
                grant role caseId
            | _ -> None)
            token

    let encodeAuthorityHistory installation (actor: ActorId option) sequence =
        encode
            installation
            [
                "actor",
                actor
                |> Option.map (ActorId.format >> JsonValue.String)
                |> Option.defaultValue JsonValue.Null
                "after", JsonValue.Integer(Sequence.value sequence)
                "kind", JsonValue.String "AUTHORITY_HISTORY"
                "v", JsonValue.Integer 2L
            ]

    let decodeAuthorityHistory installation (actor: ActorId option) token =
        let expected =
            actor
            |> Option.map (ActorId.format >> JsonValue.String)
            |> Option.defaultValue JsonValue.Null

        decode
            installation
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ "actor", target
                                 "after", JsonValue.Integer sequence
                                 "kind", JsonValue.String "AUTHORITY_HISTORY"
                                 "v", JsonValue.Integer 2L ] when
                target = expected && sequence >= 1L
                ->
                Sequence.create sequence
            | _ -> None)
            token

    let encodeReminders installation scope due id =
        encode
            installation
            [
                "kind", JsonValue.String "REMINDERS"
                "v", JsonValue.Integer 1L
                "scope", JsonValue.String scope
                "due", JsonValue.String(CalendarDate.format due)
                "reminderId", JsonValue.String(ReminderId.format id)
            ]

    let decodeReminders installation scope token =
        decode
            installation
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ "due", JsonValue.String due
                                 "kind", JsonValue.String "REMINDERS"
                                 "reminderId", JsonValue.String id
                                 "scope", JsonValue.String expected
                                 "v", JsonValue.Integer 1L ] when expected = scope ->
                match CalendarDate.parse due, uuid ReminderId.create id with
                | Ok due, Some id -> Some(due, id)
                | _ -> None
            | _ -> None)
            token

    let private changeRange =
        function
        | None -> JsonValue.Null
        | Some range ->
            let entity, reminder =
                match range.Entity with
                | ChangeEntity.Case -> "CASE", None
                | ChangeEntity.Work -> "WORK", None
                | ChangeEntity.Reminder id -> "REMINDER", Some id

            JsonValue.Object
                [
                    "actualRevision", JsonValue.Integer(Revision.value range.ActualRevision)
                    "entity", JsonValue.String entity
                    "expectedRevision", JsonValue.Integer(Revision.value range.ExpectedRevision)
                    "reminderId",
                    reminder
                    |> Option.map (ReminderId.format >> JsonValue.String)
                    |> Option.defaultValue JsonValue.Null
                ]

    let private readChangeRange =
        function
        | JsonValue.Null -> Some None
        | JsonValue.Object [ "actualRevision", JsonValue.Integer actual
                             "entity", JsonValue.String entity
                             "expectedRevision", JsonValue.Integer expected
                             "reminderId", reminder ] ->
            let target =
                match entity, reminder with
                | "CASE", JsonValue.Null -> Some ChangeEntity.Case
                | "WORK", JsonValue.Null -> Some ChangeEntity.Work
                | "REMINDER", JsonValue.String raw ->
                    uuid ReminderId.create raw |> Option.map ChangeEntity.Reminder
                | _ -> None

            match target, Revision.create expected, Revision.create actual with
            | Some entity, Some expected, Some actual ->
                Some(
                    Some
                        {
                            Entity = entity
                            ExpectedRevision = expected
                            ActualRevision = actual
                        }
                )
            | _ -> None
        | _ -> None

    let encodePayments installation parent position =
        encode
            installation
            [
                "kind", JsonValue.String "CASE_PAYMENTS"
                "caseId", JsonValue.String(CaseId.format parent)
                "selection", JsonValue.String "ACTIVE"
                "order", JsonValue.String "EFFECT_DESC"
                "beforeSequence",
                JsonValue.Integer(Sequence.value (EffectPosition.sequence position))
                "beforeEffect", JsonValue.Integer(int64 (EffectPosition.index position))
            ]

    let decodePayments installation parent token =
        decode
            installation
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ "beforeEffect", JsonValue.Integer index
                                 "beforeSequence", JsonValue.Integer sequence
                                 "caseId", JsonValue.String id
                                 "kind", JsonValue.String "CASE_PAYMENTS"
                                 "order", JsonValue.String "EFFECT_DESC"
                                 "selection", JsonValue.String "ACTIVE" ] when
                id = CaseId.format parent && index >= 0L && index <= int64 System.Int32.MaxValue
                ->
                Sequence.create sequence
                |> Option.bind (fun sequence -> EffectPosition.create sequence (int index))
            | _ -> None)
            token

    let encodeNotes installation parent sequence =
        encode
            installation
            [
                "kind", JsonValue.String "CASE_NOTES"
                "caseId", JsonValue.String(CaseId.format parent)
                "before", JsonValue.Integer(Sequence.value sequence)
            ]

    let decodeNotes installation parent token =
        decode
            installation
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ "before", JsonValue.Integer before
                                 "caseId", JsonValue.String id
                                 "kind", JsonValue.String "CASE_NOTES" ] when
                id = CaseId.format parent && before >= 1L
                ->
                Sequence.create before
            | _ -> None)
            token

    let encodeTimeline installation scope range sequence =
        encode
            installation
            [
                "kind", JsonValue.String "CASE_TIMELINE"
                "v", JsonValue.Integer 1L
                "scope", JsonValue.String scope
                "range", changeRange range
                "before", JsonValue.Integer(Sequence.value sequence)
            ]

    let decodeTimeline installation scope token =
        decode
            installation
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ "before", JsonValue.Integer before
                                 "kind", JsonValue.String "CASE_TIMELINE"
                                 "range", range
                                 "scope", JsonValue.String expected
                                 "v", JsonValue.Integer 1L ] when expected = scope && before >= 1L ->
                match Sequence.create before, readChangeRange range with
                | Some before, Some range ->
                    Some
                        {
                            BeforeSequence = before
                            Range = range
                        }
                | _ -> None
            | _ -> None)
            token
