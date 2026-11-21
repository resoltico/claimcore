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

    let decimalTotal signed =
        let grammar, parse, name, extra =
            if signed then
                DecimalTotal.signedGrammar, DecimalTotal.parseSigned, "DecimalTotal", 1
            else
                DecimalTotal.Grammar, DecimalTotal.parse, "NonnegativeDecimalTotal", 0

        patternedText
            1
            (DecimalTotal.MaximumIntegerDigits
             + DecimalTotal.MaximumFractionalDigits
             + 1
             + extra)
            ("^(?:" + grammar + ")(?![\\s\\S])")
            None
        |> interpret
            (fun path raw ->
                match parse raw with
                | Ok value when DecimalTotal.format value = raw -> Ok value
                | _ ->
                    Error
                        {
                            Path = path
                            Reason = "Expected a canonical exact decimal total."
                        })
            DecimalTotal.format
        |> named name

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

    let caseId = RequestCodecs.caseId
    let noteId = uuid |> map (NoteId.create >> Option.get) NoteId.value
    let linkId = uuid |> map (LinkId.create >> Option.get) LinkId.value
    let reminderId = uuid |> map (ReminderId.create >> Option.get) ReminderId.value
    let actorId = RequestCodecs.actorId
    let nonempty = boundedText 1 Int32.MaxValue

    let private amountMaximumCharacters =
        Money.MaximumIntegerDigits + 1 + Money.MaximumFractionalDigits

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

    let date = CalendarDateCodecs.text

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

    let calendarDate = CalendarDateCodecs.value
    let caseReference = parsed CaseReference.parse CaseReference.format reference
    let countryCode = parsed CountryCode.parse CountryCode.format country

    let partyName =
        parsed PartyName.parse PartyName.format (boundedUtf8Text 1 PartyName.MaximumBytes)

    let partyId = uuid |> map (PartyId.create >> Option.get) PartyId.value

    let partyContent =
        (record PartyContent.create: Fields<PartyContent, _>)
        |> field "role" _.Role (VocabularyCodec.create PartyRole.vocabulary)
        |> field "name" _.Name (nullable partyName)
        |> field
            "details"
            _.Details
            (nullable (
                parsed
                    PartyDetails.parse
                    PartyDetails.format
                    (boundedUtf8Text 1 PartyDetails.constraints.MaximumUtf8Bytes)
            ))
        |> buildResult (fun path _ ->
            {
                Path = path
                Reason = "At least one party name or detail must be present."
            })
        |> named "PartyContent"

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
