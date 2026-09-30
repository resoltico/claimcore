namespace ClaimCore.Witness

module internal WitnessAuditAdmission =
    let private scripts =
        lazy
            {
                Structure =
                    WitnessAdmission.resourceText "ClaimCore.Witness.AuditAdmission.Structure.sql"
                Liveness =
                    WitnessAdmission.resourceText "ClaimCore.Witness.AuditAdmission.Liveness.sql"
            }

    let script () = scripts.Value
