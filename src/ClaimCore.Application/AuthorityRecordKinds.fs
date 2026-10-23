namespace ClaimCore.Application

open ClaimCore.Contract
open ClaimCore.RecordFormat

module internal AuthorityRecordKinds =
    let internal fromRecord =
        function
        | RecordedAuthorityKind.ProvisionOwner -> AuthorityKind.ProvisionOwner
        | RecordedAuthorityKind.RegisterActor -> AuthorityKind.RegisterActor
        | RecordedAuthorityKind.SetGrant -> AuthorityKind.SetGrant
        | RecordedAuthorityKind.SetEnabled -> AuthorityKind.SetEnabled
