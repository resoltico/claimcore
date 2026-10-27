namespace ClaimCore.Tests.Architecture.OwnershipFixtures

type Used = { Value: int }
type CoreDetail = { Delta: int }
type DetailAlias = CoreDetail

type PresentationOnly = { Label: string }

module Mixed =
    let core value : Used =
        let initial = value + 1
        let detail: DetailAlias = { Delta = initial }
        { Value = detail.Delta }

    let presentation () : PresentationOnly = { Label = "presentation only" }

module Core =
    let read () = Mixed.core 1

module Presentation =
    let read () = Mixed.presentation ()
