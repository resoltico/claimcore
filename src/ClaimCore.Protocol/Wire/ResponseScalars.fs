namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.Protocol
open ClaimCore.Json

open System
open System.Globalization
open System.Text.RegularExpressions

/// Permanent response grammars describe accepted values, independently of current admission policy.
module ResponseScalars =
    open Codec

    let revision =
        RequestCodecs.revision |> map (Revision.create >> Option.get) Revision.value

    let positiveRevision =
        RequestCodecs.sequence |> map (Revision.create >> Option.get) Revision.value

    let sequence =
        RequestCodecs.sequence |> map (Sequence.create >> Option.get) Sequence.value

    let operationId = uuid |> map (OperationId.create >> Option.get) OperationId.value
    let actorId = uuid |> map (ActorId.create >> Option.get) ActorId.value
    let nonempty = boundedText 1 Int32.MaxValue

    let private amountMaximumCharacters =
        Money.MaximumIntegerDigits + 1 + Money.MaximumFractionalDigits

    let private dateCharacters = CalendarDate.ExactFormat.Length

    let private grammar minimum maximum pattern format =
        let anchored = "^(?:" + pattern + ")(?![\\s\\S])"
        let expression = Regex(anchored, RegexOptions.CultureInvariant)
        let schema = Schema.String(Some anchored, format, Some minimum, maximum)
        nonempty |> constrained schema expression.IsMatch

    let reference =
        grammar 1 (Some CaseReference.MaximumCharacters) CaseReference.Grammar None

    let country =
        grammar CountryCode.Characters (Some CountryCode.Characters) CountryCode.Grammar None

    let currency =
        grammar
            Money.CurrencyCharacters
            (Some Money.CurrencyCharacters)
            Money.descriptor.Currency.Grammar
            None

    let amount =
        grammar 1 (Some amountMaximumCharacters) Money.descriptor.Amount.Grammar None

    let digest = grammar 64 (Some 64) "[0-9a-f]{64}" None

    let date =
        grammar dateCharacters (Some dateCharacters) CalendarDate.Grammar (Some "date")
        |> constrained
            (Schema.String(
                Some("^" + CalendarDate.Grammar + "(?![\\s\\S])"),
                Some "date",
                Some dateCharacters,
                Some dateCharacters
            ))
            (fun value -> CalendarDate.parse value |> Result.isOk)

    let private parsed parse format codec =
        codec
        |> mapResult
            (fun path raw ->
                parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "value outside its wire grammar"
                    }))
            format

    let calendarDate = parsed CalendarDate.parse CalendarDate.format date
    let caseReference = parsed CaseReference.parse CaseReference.format reference
    let countryCode = parsed CountryCode.parse CountryCode.format country
    let partyName = parsed PartyName.parse PartyName.format nonempty



    let fixedField name token (fields: Fields<'Whole, 'Ctor>) : Fields<'Whole, 'Ctor> =
        let codec = constant token

        { fields with
            ReadFields =
                fun path element ->
                    fields.ReadFields path element
                    |> Result.bind (fun ctor ->
                        match JsonValue.tryMember name element with
                        | Some value ->
                            codec.Read (path + "." + name) value |> Result.map (fun () -> ctor)
                        | _ ->
                            Error
                                {
                                    Path = path
                                    Reason = "missing member " + name
                                })
            WriteFields = fun value -> fields.WriteFields value @ [ name, codec.Write() ]
            Properties = fields.Properties @ [ name, codec.Schema ]
        }
