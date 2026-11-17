namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

/// Explicit attachments for the compositions used by existing interfaces.
module ClientCompositions =
    let private declared token =
        let id = TaskId.ofLiteral token

        ClaimCore.Domain.Tasks.Registry.findDeclaration id
        |> Option.defaultWith (fun () -> invalidOp "A composition requires a declared ledger task.")
        |> _.Id

    let accountRegistration = declared "people.register"
    let admission = declared "people.admit"
    let noteEntry = declared "note.add"
    let assignment = declared "work.update"
    let reminderCreation = declared "reminder.create"
    let noteRemoval = declared "note.remove"
    let paymentRemoval = declared "case.removePayment"
    let paymentCorrection = declared "case.correctPayment"
