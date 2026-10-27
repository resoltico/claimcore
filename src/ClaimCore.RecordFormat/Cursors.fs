namespace ClaimCore.RecordFormat

open ClaimCore.Json

open System
open ClaimCore.Domain

type GrantPosition =
    {
        Role: Role
        CaseId: System.Guid option
    }

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

    let private decode read (token: string) =
        if
            String.IsNullOrEmpty token
            || token.Length > MaximumTokenCharacters
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

                match StrictJson.read MaximumTokenCharacters bytes with
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
             Refusal.InvalidInput(InputTarget.Field BusinessField.CaseReference, violation)))
            value
        |> Result.isOk
        && CaseReference.key value = value

    let private uuid value =
        match Guid.TryParseExact((value: string), "D") with
        | true, id when id <> Guid.Empty && id.ToString("D") = value -> Some id
        | _ -> None

    let encodeCases after =
        encode
            [
                DurableNames.After, JsonValue.String(CaseReference.key after)
                DurableNames.Kind, JsonValue.String "CASES"
                DurableNames.V, JsonValue.Integer 1L
            ]

    let decodeCases token =
        decode
            (function
            | JsonValue.Object [ DurableNames.After, JsonValue.String after
                                 DurableNames.Kind, JsonValue.String "CASES"
                                 DurableNames.V, JsonValue.Integer 1L ] when reference after ->
                Some after
            | _ -> None)
            token

    let encodeHistory reference after =
        encode
            [
                DurableNames.After, JsonValue.Integer after
                DurableNames.Case, JsonValue.String(CaseReference.key reference)
                DurableNames.Kind, JsonValue.String "HISTORY"
                DurableNames.V, JsonValue.Integer 1L
            ]

    let decodeHistory requested token =
        decode
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
                Some after
            | _ -> None)
            token

    let encodeActors (after: Guid) =
        encode
            [
                DurableNames.After, JsonValue.String(after.ToString("D"))
                DurableNames.Kind, JsonValue.String "ACTORS"
                DurableNames.V, JsonValue.Integer 1L
            ]

    let decodeActors token =
        decode
            (function
            | JsonValue.Object [ DurableNames.After, JsonValue.String after
                                 DurableNames.Kind, JsonValue.String "ACTORS"
                                 DurableNames.V, JsonValue.Integer 1L ] -> uuid after
            | _ -> None)
            token

    let encodeGrants (actorId: Guid) (after: GrantPosition) =
        let position =
            JsonValue.Object
                [
                    DurableNames.CaseId,
                    after.CaseId
                    |> Option.map (fun id -> JsonValue.String(id.ToString("D")))
                    |> Option.defaultValue JsonValue.Null
                    DurableNames.Role, JsonValue.String(DurableNames.role after.Role)
                ]

        encode
            [
                DurableNames.Actor, JsonValue.String(actorId.ToString("D"))
                DurableNames.After, position
                DurableNames.Kind, JsonValue.String "GRANTS"
                DurableNames.V, JsonValue.Integer 1L
            ]

    let private grant role caseId =
        let scope =
            match caseId with
            | JsonValue.Null -> Some None
            | JsonValue.String value -> uuid value |> Option.map Some
            | _ -> None

        match DurableNames.tryRole role, scope with
        | Some role, Some caseId -> Some { Role = role; CaseId = caseId }
        | _ -> None

    let decodeGrants actorId token =
        decode
            (function
            | JsonValue.Object [ DurableNames.Actor, JsonValue.String target
                                 DurableNames.After,
                                 JsonValue.Object [ DurableNames.CaseId, caseId
                                                    DurableNames.Role, JsonValue.String role ]
                                 DurableNames.Kind, JsonValue.String "GRANTS"
                                 DurableNames.V, JsonValue.Integer 1L ] when
                uuid target = Some actorId
                ->
                grant role caseId
            | _ -> None)
            token
