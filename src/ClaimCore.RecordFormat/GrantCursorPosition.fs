namespace ClaimCore.RecordFormat

open System.Text

/// Primitive continuation fields; Application owns role and identifier admission.
module GrantCursorPosition =
    let encode role caseId =
        CanonicalValue.Object
            [
                "role", CanonicalValue.String role
                "caseId",
                caseId
                |> Option.map CanonicalValue.String
                |> Option.defaultValue CanonicalValue.Null
            ]
        |> CanonicalJson.encode
        |> Encoding.UTF8.GetString

    let decode value =
        let read root =
            Json.properties "$" [ "role"; "caseId" ] root
            Json.text "$" "role" root, Json.optionalString "$" "caseId" root

        Json.bounded CursorRecord.MaximumPositionBytes (Encoding.UTF8.GetBytes(value: string)) read
        |> Result.bind (fun (role, caseId) ->
            if encode role caseId = value then
                Ok(role, caseId)
            else
                Error
                    {
                        Field = "$"
                        Message = "Use a canonical grant continuation."
                    })
