namespace ClaimCore.Domain

/// Field adapters refer to descriptors owned by value modules.
module FieldRules =
    let token grammar : ScalarTextConstraints =
        {
            Format =
                {
                    MinimumCharacters = 1
                    Grammar = Some grammar
                }
            MaximumCharacters = None
        }

    let freeText = PartyName.descriptor
    let calendarDate = CalendarDate.descriptor
    let amount = Money.descriptor.Amount
    let currency = Money.descriptor.Currency
