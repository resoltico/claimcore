namespace ClaimCore.Domain

open System.Text

/// Permanent Unicode interchange rules are shared by scalar and JSON boundaries.
module UnicodeText =
    let isInterchange (value: string) =
        if obj.ReferenceEquals(value, null) then
            false
        else
            try
                UTF8Encoding(false, true).GetByteCount(value) |> ignore

                value.EnumerateRunes()
                |> Seq.forall (fun rune ->
                    not (rune.Value >= 0xFDD0 && rune.Value <= 0xFDEF)
                    && (rune.Value &&& 0xFFFE) <> 0xFFFE)
            with :? EncoderFallbackException ->
                false

    /// PostgreSQL text additionally cannot represent U+0000. Generic JSON may escape it.
    let isPersisted (value: string) =
        isInterchange value && not (value.Contains '\u0000')
