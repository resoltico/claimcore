namespace ClaimCore.Application

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
        CanonicalValue.Object members |> CanonicalJson.encode |> base64

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
            Error Rejection.InvalidCursor
        else
            try
                let padded =
                    token.Replace('-', '+').Replace('_', '/')
                    + String('=', (4 - token.Length % 4) % 4)

                let bytes = Convert.FromBase64String padded
                UTF8Encoding(false, true).GetCharCount(bytes) |> ignore

                use document =
                    JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth = 32))

                let value = CanonicalJson.ofElement document.RootElement

                if base64 bytes <> token || CanonicalJson.encode value <> bytes then
                    Error Rejection.InvalidCursor
                else
                    match read value with
                    | Some position -> Ok position
                    | None -> Error Rejection.InvalidCursor
            with
            | :? ArgumentException
            | :? FormatException
            | :? InvalidOperationException
            | :? JsonException -> Error Rejection.InvalidCursor

    let private reference value =
        CaseReference.validate value |> Result.isOk && CaseReference.key value = value

    let private uuid value =
        match Guid.TryParseExact((value: string), "D") with
        | true, id when id <> Guid.Empty && id.ToString("D") = value -> Some id
        | _ -> None

    let encodeCases after =
        encode
            [
                "after", CanonicalValue.String(CaseReference.key after)
                "kind", CanonicalValue.String "CASES"
                "v", CanonicalValue.Integer 1L
            ]

    let decodeCases token =
        decode
            (function
            | CanonicalValue.Object [ "after", CanonicalValue.String after
                                      "kind", CanonicalValue.String "CASES"
                                      "v", CanonicalValue.Integer 1L ] when reference after ->
                Some after
            | _ -> None)
            token

    let encodeHistory reference after =
        encode
            [
                "after", CanonicalValue.Integer after
                "case", CanonicalValue.String(CaseReference.key reference)
                "kind", CanonicalValue.String "HISTORY"
                "v", CanonicalValue.Integer 1L
            ]

    let decodeHistory requested token =
        decode
            (function
            | CanonicalValue.Object [ "after", CanonicalValue.Integer after
                                      "case", CanonicalValue.String target
                                      "kind", CanonicalValue.String "HISTORY"
                                      "v", CanonicalValue.Integer 1L ] when
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
                "after", CanonicalValue.String(after.ToString("D"))
                "kind", CanonicalValue.String "ACTORS"
                "v", CanonicalValue.Integer 1L
            ]

    let decodeActors token =
        decode
            (function
            | CanonicalValue.Object [ "after", CanonicalValue.String after
                                      "kind", CanonicalValue.String "ACTORS"
                                      "v", CanonicalValue.Integer 1L ] -> uuid after
            | _ -> None)
            token

    let encodeGrants (actorId: Guid) (after: GrantPosition) =
        let position =
            CanonicalValue.Object
                [
                    "caseId",
                    after.CaseId
                    |> Option.map (fun id -> CanonicalValue.String(id.ToString("D")))
                    |> Option.defaultValue CanonicalValue.Null
                    "role", CanonicalValue.String(Roles.token after.Role)
                ]

        encode
            [
                "actor", CanonicalValue.String(actorId.ToString("D"))
                "after", position
                "kind", CanonicalValue.String "GRANTS"
                "v", CanonicalValue.Integer 1L
            ]

    let private grant role caseId =
        let scope =
            match caseId with
            | CanonicalValue.Null -> Some None
            | CanonicalValue.String value -> uuid value |> Option.map Some
            | _ -> None

        match Roles.tryParse role, scope with
        | Some role, Some caseId -> Some { Role = role; CaseId = caseId }
        | _ -> None

    let decodeGrants actorId token =
        decode
            (function
            | CanonicalValue.Object [ "actor", CanonicalValue.String target
                                      "after",
                                      CanonicalValue.Object [ "caseId", caseId
                                                              "role", CanonicalValue.String role ]
                                      "kind", CanonicalValue.String "GRANTS"
                                      "v", CanonicalValue.Integer 1L ] when
                uuid target = Some actorId
                ->
                grant role caseId
            | _ -> None)
            token
