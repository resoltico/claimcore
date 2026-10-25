namespace ClaimCore.Application

open ClaimCore.Contract

open System
open System.Text
open System.Text.Json
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Plain positions convey no authority. Every selection authorizes before decoding.
module internal Cursors =
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
                UTF8Encoding(false, true).GetCharCount(bytes) |> ignore

                use document =
                    JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth = 32))

                let value = StrictJson.ofElement document.RootElement

                if base64 bytes <> token || CanonicalWriter.bytes value <> bytes then
                    Error Refusal.InvalidCursor
                else
                    match read value with
                    | Some position -> Ok position
                    | None -> Error Refusal.InvalidCursor
            with
            | :? ArgumentException
            | :? FormatException
            | :? InvalidOperationException
            | :? JsonException -> Error Refusal.InvalidCursor

    let private reference value =
        CaseReference.validate value |> Result.isOk && CaseReference.key value = value

    let private uuid value =
        match Guid.TryParseExact((value: string), "D") with
        | true, id when id <> Guid.Empty && id.ToString("D") = value -> Some id
        | _ -> None

    let encodeCases after =
        encode
            [
                "after", JsonValue.String(CaseReference.key after)
                "kind", JsonValue.String "CASES"
                "v", JsonValue.Integer 1L
            ]

    let decodeCases token =
        decode
            (function
            | JsonValue.Object [ "after", JsonValue.String after
                                 "kind", JsonValue.String "CASES"
                                 "v", JsonValue.Integer 1L ] when reference after -> Some after
            | _ -> None)
            token

    let encodeHistory reference after =
        encode
            [
                "after", JsonValue.Integer after
                "case", JsonValue.String(CaseReference.key reference)
                "kind", JsonValue.String "HISTORY"
                "v", JsonValue.Integer 1L
            ]

    let decodeHistory requested token =
        decode
            (function
            | JsonValue.Object [ "after", JsonValue.Integer after
                                 "case", JsonValue.String target
                                 "kind", JsonValue.String "HISTORY"
                                 "v", JsonValue.Integer 1L ] when
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
                "after", JsonValue.String(after.ToString("D"))
                "kind", JsonValue.String "ACTORS"
                "v", JsonValue.Integer 1L
            ]

    let decodeActors token =
        decode
            (function
            | JsonValue.Object [ "after", JsonValue.String after
                                 "kind", JsonValue.String "ACTORS"
                                 "v", JsonValue.Integer 1L ] -> uuid after
            | _ -> None)
            token

    let encodeGrants (actorId: Guid) (after: GrantPosition) =
        let position =
            JsonValue.Object
                [
                    "caseId",
                    after.CaseId
                    |> Option.map (fun id -> JsonValue.String(id.ToString("D")))
                    |> Option.defaultValue JsonValue.Null
                    "role", JsonValue.String(RoleTokens.token after.Role)
                ]

        encode
            [
                "actor", JsonValue.String(actorId.ToString("D"))
                "after", position
                "kind", JsonValue.String "GRANTS"
                "v", JsonValue.Integer 1L
            ]

    let private grant role caseId =
        let scope =
            match caseId with
            | JsonValue.Null -> Some None
            | JsonValue.String value -> uuid value |> Option.map Some
            | _ -> None

        match RoleTokens.tryParse role, scope with
        | Some role, Some caseId -> Some { Role = role; CaseId = caseId }
        | _ -> None

    let decodeGrants actorId token =
        decode
            (function
            | JsonValue.Object [ "actor", JsonValue.String target
                                 "after",
                                 JsonValue.Object [ "caseId", caseId; "role", JsonValue.String role ]
                                 "kind", JsonValue.String "GRANTS"
                                 "v", JsonValue.Integer 1L ] when uuid target = Some actorId ->
                grant role caseId
            | _ -> None)
            token
