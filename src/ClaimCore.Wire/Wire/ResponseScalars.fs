namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

open System
open System.Globalization

/// Retained response grammars describe accepted values, independently of current admission policy.
module ResponseScalars =
    open Codec

    let revision =
        RequestCodecs.revision |> map (Revision.create >> Option.get) Revision.value

    let positiveRevision =
        RequestCodecs.sequence |> map (Revision.create >> Option.get) Revision.value

    let sequence =
        RequestCodecs.sequence |> map (Sequence.create >> Option.get) Sequence.value

    let operationId =
        uuid
        |> restrict
            (Schema.String(
                Some
                    "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-7[0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$",
                Some "uuid",
                Some 36,
                Some 36,
                None
            ))
            (fun path value ->
                uuid.Read path value
                |> Result.bind (fun id ->
                    if OperationId.create id |> Option.isSome then
                        Ok()
                    else
                        Error
                            {
                                Path = path
                                Reason = "Expected an RFC UUIDv7."
                            }))
        |> map (OperationId.create >> Option.get) OperationId.value

    let caseId = uuid |> map (CaseId.create >> Option.get) CaseId.value
    let noteId = uuid |> map (NoteId.create >> Option.get) NoteId.value
    let reminderId = uuid |> map (ReminderId.create >> Option.get) ReminderId.value
    let actorId = uuid |> map (ActorId.create >> Option.get) ActorId.value
    let nonempty = boundedText 1 Int32.MaxValue

    let private amountMaximumCharacters =
        Money.MaximumIntegerDigits + 1 + Money.MaximumFractionalDigits

    let private dateCharacters = CalendarDate.ExactFormat.Length

    let private grammar minimum maximum pattern format =
        let anchored = "^(?:" + pattern + ")(?![\\s\\S])"
        patternedText minimum (defaultArg maximum Int32.MaxValue) anchored format

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
        |> restrict
            (Schema.String(
                Some("^" + CalendarDate.Grammar + "(?![\\s\\S])"),
                Some "date",
                Some dateCharacters,
                Some dateCharacters,
                None
            ))
            (fun path value ->
                text.Read path value
                |> Result.bind (fun raw ->
                    if CalendarDate.parse raw |> Result.isOk then
                        Ok()
                    else
                        Error
                            {
                                Path = path
                                Reason = "Expected a Gregorian date."
                            }))

    let private parsed parse format codec =
        codec
        |> interpret
            (fun path raw ->
                parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "value outside its wire grammar"
                    }))
            format

    let noteText =
        parsed NoteText.parse NoteText.format (boundedUtf8Text 1 NoteText.MaximumBytes)

    let calendarDate = parsed CalendarDate.parse CalendarDate.format date
    let caseReference = parsed CaseReference.parse CaseReference.format reference
    let countryCode = parsed CountryCode.parse CountryCode.format country

    let partyName =
        parsed PartyName.parse PartyName.format (boundedUtf8Text 1 PartyName.MaximumBytes)

    let handlingSummary =
        parsed
            HandlingSummary.parse
            HandlingSummary.format
            (boundedUtf8Text 1 HandlingSummary.MaximumBytes)

    let reminderAction =
        parsed
            ReminderAction.parse
            ReminderAction.format
            (boundedUtf8Text 1 ReminderAction.MaximumBytes)

    let money =
        (record Money.create: Fields<Money, _>)
        |> field "amount" Money.amount (parsed Amount.parse Amount.format amount)
        |> field "currency" Money.currencyValue (parsed Currency.parse Currency.format currency)
        |> build
        |> named "Money"



    let fixedField name token (fields: Fields<'Whole, 'Ctor>) : Fields<'Whole, 'Ctor> =
        let codec = constant token

        { fields with
            Validate =
                fun path element ->
                    fields.Validate path element
                    |> Result.bind (fun () ->
                        match JsonValue.tryMember name element with
                        | Some value -> codec.Validate (path + "." + name) value
                        | None ->
                            Error
                                {
                                    Path = path
                                    Reason = "missing member " + name
                                })
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

    let workDateBasis =
        (record (fun observed date zone ->
            {
                WorkDateBasis.ObservedAt = observed
                BusinessDate = date
                TimeZoneId = zone
            })
        : Fields<WorkDateBasis, _>)
        |> field "observedAt" _.ObservedAt instant
        |> field
            "businessDate"
            _.BusinessDate
            (calendarDate |> map CalendarDate.value CalendarDate.ofDate)
        |> field "timeZone" _.TimeZoneId nonempty
        |> build
        |> named "WorkDateBasis"
