namespace ClaimCore.RecordFormat

open ClaimCore.Json

open System
open System.Globalization
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type CaseListPosition =
    | Reference of string
    | Recent of changedAt: DateTimeOffset * reference: string

module CaseListPosition =
    let reference =
        function
        | CaseListPosition.Reference reference
        | CaseListPosition.Recent(_, reference) -> reference

    let create order changedAt reference =
        match order with
        | CaseListOrder.Reference -> CaseListPosition.Reference(CaseReference.key reference)
        | CaseListOrder.Recent -> CaseListPosition.Recent(changedAt, CaseReference.key reference)

type GrantPosition = { Role: Role; CaseId: CaseId option }

/// Plain positions convey no authority. Every selection authorizes before decoding.
module Cursors =
    [<Literal>]
    let MaximumTokenCharacters = 512

    let private base64 (bytes: byte array) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private encode members =
        JsonValue.Object members |> CanonicalWriter.bytes |> base64

    let private alphabet character =
        (character >= 'A' && character <= 'Z')
        || (character >= 'a' && character <= 'z')
        || (character >= '0' && character <= '9')
        || character = '-'
        || character = '_'

    let private decode maximum read (token: string) =
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
                        match read value with
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

    let encodeCases (filter: CaseListFilter<CaseSearchText>) position =
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
            | _ -> invalidArg (nameof position) "The position must match the selected order."

        encode
            [
                DurableNames.After, after
                "filters", caseFilters filter
                DurableNames.Kind, JsonValue.String "CASES"
                DurableNames.V, JsonValue.Integer 2L
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
        | _ -> None

    let decodeCases (filter: CaseListFilter<CaseSearchText>) token =
        decode
            Paging.MaximumCaseCursorCharacters
            (function
            | JsonValue.Object [ DurableNames.After, after
                                 "filters", filters
                                 DurableNames.Kind, JsonValue.String "CASES"
                                 DurableNames.V, JsonValue.Integer 2L ] when
                CanonicalWriter.bytes filters = CanonicalWriter.bytes (caseFilters filter)
                ->
                casePosition filter.Order after
            | _ -> None)
            token

    let encodeHistory reference after =
        encode
            [
                DurableNames.After, JsonValue.Integer(Revision.value after)
                DurableNames.Case, JsonValue.String(CaseReference.key reference)
                DurableNames.Kind, JsonValue.String "HISTORY"
                DurableNames.V, JsonValue.Integer 1L
            ]

    let decodeHistory requested token =
        decode
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ DurableNames.After, JsonValue.Integer after
                                 DurableNames.Case, JsonValue.String target
                                 DurableNames.Kind, JsonValue.String "HISTORY"
                                 DurableNames.V, JsonValue.Integer 1L ] when
                after >= 1L
                && after <= Revision.Maximum
                && reference target
                && target = CaseReference.key requested
                ->
                Revision.create after
            | _ -> None)
            token

    let encodeActors (after: ActorId) =
        encode
            [
                DurableNames.After, JsonValue.String(ActorId.format after)
                DurableNames.Kind, JsonValue.String "ACTORS"
                DurableNames.V, JsonValue.Integer 1L
            ]

    let decodeActors token =
        decode
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ DurableNames.After, JsonValue.String after
                                 DurableNames.Kind, JsonValue.String "ACTORS"
                                 DurableNames.V, JsonValue.Integer 1L ] -> uuid ActorId.create after
            | _ -> None)
            token

    let encodeGrants (actorId: ActorId) (after: GrantPosition) =
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
            [
                DurableNames.Actor, JsonValue.String(ActorId.format actorId)
                DurableNames.After, position
                DurableNames.Kind, JsonValue.String "GRANTS"
                DurableNames.V, JsonValue.Integer 1L
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

    let decodeGrants actorId token =
        decode
            MaximumTokenCharacters
            (function
            | JsonValue.Object [ DurableNames.Actor, JsonValue.String target
                                 DurableNames.After,
                                 JsonValue.Object [ DurableNames.CaseId, caseId
                                                    DurableNames.Role, JsonValue.String role ]
                                 DurableNames.Kind, JsonValue.String "GRANTS"
                                 DurableNames.V, JsonValue.Integer 1L ] when
                uuid ActorId.create target = Some actorId
                ->
                grant role caseId
            | _ -> None)
            token
