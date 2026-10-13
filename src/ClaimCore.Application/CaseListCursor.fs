namespace ClaimCore.Application

open System
open System.Text
open ClaimCore.Domain

module internal CaseListCursor =
    let private text = UTF8Encoding(false, true)

    let encode reference =
        Convert
            .ToBase64String(text.GetBytes(reference: string))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    let decode (token: string) =
        if String.IsNullOrEmpty token || token.Length > 428 then
            Error()
        else
            try
                let raw = token.Replace('-', '+').Replace('_', '/')

                let bytes =
                    Convert.FromBase64String(raw + String.replicate ((4 - raw.Length % 4) % 4) "=")

                let reference = text.GetString bytes

                if Claim.validateReference reference = Ok() && encode reference = token then
                    Ok reference
                else
                    Error()
            with
            | :? FormatException
            | :? DecoderFallbackException -> Error()
