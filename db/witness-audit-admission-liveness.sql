SELECT current_setting('server_version_num')::integer BETWEEN 180006 AND 189999
AND current_user='claimcore_witness_auditor'
AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname=current_user
    AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole
    AND NOT rolreplication AND NOT rolbypassrls AND NOT rolinherit)
AND EXISTS (SELECT 1 FROM claimcore_witness.installation WHERE singleton
    AND installation_id=@installation AND lineage_id=@lineage AND epoch=@epoch
    AND baseline_id='claimcore-witness-v1' AND baseline_sha256=@digest
    AND ((tip_sequence=0 AND tip_hash=decode(repeat('00',32),'hex'))
       OR (tip_sequence>0 AND EXISTS (SELECT 1 FROM claimcore_witness.journal j
           WHERE j.installation_id=@installation AND j.sequence=tip_sequence
             AND j.entry_hash=tip_hash))))
