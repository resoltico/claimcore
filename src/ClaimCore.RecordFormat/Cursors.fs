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

module CaseListPosition =
    let reference =
        function
        | CaseListPosition.Reference reference
        | CaseListPosition.Recent(_, reference)
        | CaseListPosition.Notified(_, reference) -> reference

    let create order changedAt notifiedOn reference =
        match order with
        | CaseListOrder.Reference -> CaseListPosition.Reference(CaseReference.key reference)
        | CaseListOrder.Recent -> CaseListPosition.Recent(changedAt, CaseReference.key reference)
        | CaseListOrder.Notified ->
            CaseListPosition.Notified(notifiedOn, CaseReference.key reference)

type GrantPosition = { Role: Role; CaseId: CaseId option }

/// Plain positions convey no authority. Every selection authorizes before decoding.
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
                JsonValue.Array(
                    filter.Lifecycles |> List.map (DurableNames.lifecycle >> JsonValue.String)
                )
                "status",
                JsonValue.Array(
                    filter.Statuses |> List.map (DurableNames.status >> JsonValue.String)
                )
                "progress",
                JsonValue.Array(
                    filter.Progress |> List.map (DurableNames.progress >> JsonValue.String)
                )
                "text",
                filter.Text
                |> Option.map (CaseSearchText.value >> JsonValue.String)
                |> Option.defaultValue JsonValue.Null
                "order", JsonValue.String(DurableNames.caseOrder filter.Order)
                "assignment",
                filter.Assignment
                |> Option.map (DurableNames.caseAssignment >> JsonValue.String)
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
            | _ -> invalidArg (nameof position) "The position must match the selected order."

        encode
            installation
            [
                DurableNames.After, after
                "filters", caseFilters filter
                DurableNames.Kind, JsonValue.String "CASES"
                DurableNames.V, JsonValue.Integer 4L
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
        | _ -> None

    let decodeCases installation (filter: CaseListFilter<CaseSearchText>) token =
        decode
            installation
            16384
            (function
            | JsonValue.Object [ DurableNames.After, after
                                 "filters", filters
                                 DurableNames.Kind, JsonValue.String "CASES"
                                 DurableNames.V, JsonValue.Integer 4L ] when
                CanonicalWriter.bytes filters = CanonicalWriter.bytes (caseFilters filter)
                ->
                casePosition filter.Order after
            | _ -> None)
            token

    let encodeHistory installation (caseId: CaseId) after =
        encode
            installation
            [
                DurableNames.After, JsonValue.Integer(Revision.value after)
                DurableNames.CaseId, JsonValue.String(CaseId.format caseId)
                DurableNames.Kind, JsonValue.String "HISTORY"
                DurableNames.V, JsonValue.Integer 2L
            ]

    let decodeHistory installation requested token =
        decode
            installation
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ DurableNames.After, JsonValue.Integer after
                                 DurableNames.CaseId, JsonValue.String target
                                 DurableNames.Kind, JsonValue.String "HISTORY"
                                 DurableNames.V, JsonValue.Integer 2L ] when
                after >= 1L
                && after <= Revision.Maximum
                && uuid CaseId.create target = Some requested
                ->
                Revision.create after
            | _ -> None)
            token

    let encodeActors installation (after: ActorId) =
        encode
            installation
            [
                DurableNames.After, JsonValue.String(ActorId.format after)
                DurableNames.Kind, JsonValue.String "ACTORS"
                DurableNames.V, JsonValue.Integer 2L
            ]

    let decodeActors installation token =
        decode
            installation
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ DurableNames.After, JsonValue.String after
                                 DurableNames.Kind, JsonValue.String "ACTORS"
                                 DurableNames.V, JsonValue.Integer 2L ] -> uuid ActorId.create after
            | _ -> None)
            token

    let encodeGrants installation (actorId: ActorId) (after: GrantPosition) =
        let position =
            JsonValue.Object
                [
                    DurableNames.CaseId,
                    after.CaseId
                    |> Option.map (fun id -> JsonValue.String(CaseId.format id))
                    |> Option.defaultValue JsonValue.Null
                    DurableNames.Role, JsonValue.String(DurableNames.role after.Role)
                ]

        encode
            installation
            [
                DurableNames.Actor, JsonValue.String(ActorId.format actorId)
                DurableNames.After, position
                DurableNames.Kind, JsonValue.String "GRANTS"
                DurableNames.V, JsonValue.Integer 2L
            ]

    let private grant role caseId =
        let scope =
            match caseId with
            | JsonValue.Null -> Some None
            | JsonValue.String value -> uuid CaseId.create value |> Option.map Some
            | _ -> None

        match DurableNames.tryRole role, scope with
        | Some role, Some caseId -> Some { Role = role; CaseId = caseId }
        | _ -> None

    let decodeGrants installation actorId token =
        decode
            installation
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ DurableNames.Actor, JsonValue.String target
                                 DurableNames.After,
                                 JsonValue.Object [ DurableNames.CaseId, caseId
                                                    DurableNames.Role, JsonValue.String role ]
                                 DurableNames.Kind, JsonValue.String "GRANTS"
                                 DurableNames.V, JsonValue.Integer 2L ] when
                uuid ActorId.create target = Some actorId
                ->
                grant role caseId
            | _ -> None)
            token

    let encodeAuthorityHistory installation (actor: ActorId option) sequence =
        encode
            installation
            [
                DurableNames.Actor,
                actor
                |> Option.map (ActorId.format >> JsonValue.String)
                |> Option.defaultValue JsonValue.Null
                DurableNames.After, JsonValue.Integer(Sequence.value sequence)
                DurableNames.Kind, JsonValue.String "AUTHORITY_HISTORY"
                DurableNames.V, JsonValue.Integer 2L
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
            | JsonValue.Object [ DurableNames.Actor, target
                                 DurableNames.After, JsonValue.Integer sequence
                                 DurableNames.Kind, JsonValue.String "AUTHORITY_HISTORY"
                                 DurableNames.V, JsonValue.Integer 2L ] when
                target = expected && sequence >= 1L
                ->
                Sequence.create sequence
            | _ -> None)
            token
