namespace ClaimCore.Application

open ClaimCore.Contract
open ClaimCore.RecordFormat

module internal AuthorityRecordKinds =
    let internal fromRecord =
        function
        | AuthorityKind.ProvisionOwner -> AuthorityKind.ProvisionOwner
        | AuthorityKind.RegisterActor -> AuthorityKind.RegisterActor
        | AuthorityKind.SetGrant -> AuthorityKind.SetGrant
        | AuthorityKind.SetEnabled -> AuthorityKind.SetEnabled
