SELECT current_setting('fsync') = 'on'
AND current_setting('full_page_writes') = 'on'
AND current_setting('synchronous_commit') = 'on'
AND current_setting('default_transaction_read_only') = 'off'
AND current_setting('server_version_num')::integer BETWEEN 180006
AND 189999
AND NOT pg_is_in_recovery()
AND current_user = 'claimcore_witness_writer'
AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname=current_user
AND NOT rolsuper
AND NOT rolcreatedb
AND NOT rolcreaterole
AND NOT rolreplication
AND NOT rolbypassrls
AND NOT rolinherit)
AND NOT has_database_privilege(current_user,current_database(),'CREATE')
AND NOT has_schema_privilege(current_user,'claimcore_witness','CREATE')
AND NOT has_table_privilege(current_user,'claimcore_witness.journal','INSERT')
AND NOT has_table_privilege(current_user,'claimcore_witness.journal','UPDATE')
AND NOT has_table_privilege(current_user,'claimcore_witness.journal','DELETE')
AND NOT has_table_privilege(current_user,'claimcore_witness.journal','TRUNCATE')
AND has_table_privilege(current_user,'claimcore_witness.journal','SELECT')
AND NOT has_table_privilege(current_user,'claimcore_witness.journal_payloads','INSERT')
AND NOT has_table_privilege(current_user,'claimcore_witness.journal_payloads','UPDATE')
AND NOT has_table_privilege(current_user,'claimcore_witness.journal_payloads','DELETE')
AND NOT has_table_privilege(current_user,'claimcore_witness.journal_payloads','TRUNCATE')
AND has_table_privilege(current_user,'claimcore_witness.journal_payloads','SELECT')
AND has_table_privilege(current_user,'claimcore_witness.installation','SELECT')
AND has_table_privilege(current_user,'claimcore_witness.writer_handoffs','SELECT')
AND NOT has_table_privilege(current_user,'claimcore_witness.writer_handoffs','INSERT')
AND NOT has_table_privilege(current_user,'claimcore_witness.writer_handoffs','UPDATE')
AND NOT has_table_privilege(current_user,'claimcore_witness.writer_handoffs','DELETE')
AND NOT has_table_privilege(current_user,'claimcore_witness.writer_handoffs','TRUNCATE')
AND has_function_privilege(current_user,'claimcore_witness.append(uuid,uuid,bigint,uuid,text,uuid,text,uuid,bytea,bytea)','EXECUTE')
AND has_function_privilege(current_user,'claimcore_witness.acquire_read_fence(uuid,uuid,bigint,bytea)','EXECUTE')
AND (SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname='claimcore_witness') = 'claimcore_witness_owner'
AND (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace   WHERE n.nspname='claimcore_witness') = 17
AND (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace   WHERE n.nspname='claimcore_witness') = 10
AND NOT EXISTS (SELECT 1 FROM pg_policy pol JOIN pg_class c ON c.oid=pol.polrelid   JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='claimcore_witness')
AND NOT EXISTS (SELECT 1 FROM pg_default_acl WHERE defaclnamespace='claimcore_witness'::regnamespace)
AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace   WHERE n.nspname='claimcore_witness'
AND (c.relrowsecurity OR c.relforcerowsecurity     OR (c.relkind='r'
AND c.relowner::regrole::text <> 'claimcore_witness_owner')))
AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace   CROSS JOIN LATERAL aclexplode(c.relacl) acl   WHERE n.nspname='claimcore_witness'
AND (acl.grantee=0     OR acl.grantee NOT IN ('claimcore_witness_owner'::regrole,'claimcore_witness_writer'::regrole,'claimcore_witness_auditor'::regrole)     OR (acl.grantee IN ('claimcore_witness_writer'::regrole,'claimcore_witness_auditor'::regrole)
AND acl.privilege_type <> 'SELECT')))
AND NOT EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid   JOIN pg_namespace n ON n.oid=c.relnamespace   WHERE n.nspname='claimcore_witness'
AND a.attacl IS NOT NULL)
AND (SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid   JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='claimcore_witness'
AND NOT t.tgisinternal) = 0
AND EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace   WHERE n.nspname='claimcore_witness'
AND p.proname='append'
AND p.prosecdef
AND p.proowner::regrole::text='claimcore_witness_owner'
AND p.proconfig = ARRAY['search_path=pg_catalog, claimcore_witness, pg_temp']
AND NOT EXISTS (SELECT 1 FROM aclexplode(p.proacl) acl WHERE acl.grantee=0     OR acl.grantee NOT IN ('claimcore_witness_owner'::regrole,'claimcore_witness_writer'::regrole)))
AND EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='claimcore_witness' AND p.proname='acquire_read_fence'
  AND p.prosecdef AND p.proowner::regrole::text='claimcore_witness_owner'
  AND p.proconfig = ARRAY['search_path=pg_catalog, claimcore_witness, pg_temp']
  AND NOT EXISTS (SELECT 1 FROM aclexplode(p.proacl) acl WHERE acl.grantee=0
    OR acl.grantee NOT IN ('claimcore_witness_owner'::regrole,'claimcore_witness_writer'::regrole)))
AND NOT has_function_privilege(current_user,  'claimcore_witness.rotate_key(uuid,uuid,bigint,uuid,uuid,uuid,bytea,bytea,bytea)','EXECUTE')
AND NOT has_function_privilege(current_user,
  'claimcore_witness.prepare_writer_handoff(uuid,uuid,bigint,uuid,bigint,bigint,bytea,bytea,uuid,bytea,bytea,uuid,uuid,uuid,bytea,bytea)','EXECUTE')
AND NOT has_function_privilege(current_user,
  'claimcore_witness.commit_writer_handoff(uuid,uuid,bigint,uuid,bigint,bytea,bytea,bytea,bytea,bytea,uuid,bytea)','EXECUTE')
AND NOT has_function_privilege(current_user,
  'claimcore_witness.activate_writer_handoff(uuid,uuid,bigint,uuid,uuid,bigint,bytea,bytea,uuid,bytea,bytea)','EXECUTE')
AND NOT has_function_privilege(current_user,
  'claimcore_witness.activate_data_use(uuid,uuid,bigint,uuid,bigint,bytea,bytea,uuid,bytea,bytea)','EXECUTE')
AND NOT has_function_privilege(current_user,
  'claimcore_witness.abort_writer_handoff(uuid,uuid,bigint,uuid,bigint,bytea,bytea,bytea,bytea,bytea,uuid,uuid,uuid,bytea)','EXECUTE')
AND NOT has_function_privilege(current_user,
  'claimcore_witness.release_aborted_writer_handoff(uuid,uuid,bigint,uuid,bigint,bytea,bytea)','EXECUTE')
AND EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='claimcore_witness' AND p.proname='settle_and_prune'
  AND p.prosecdef AND p.proowner::regrole::text='claimcore_witness_owner'
  AND p.proconfig = ARRAY['search_path=pg_catalog, claimcore_witness, pg_temp']
  AND NOT has_function_privilege(current_user,p.oid,'EXECUTE')
  AND NOT EXISTS (SELECT 1 FROM aclexplode(p.proacl) acl WHERE acl.grantee=0
    OR acl.grantee NOT IN ('claimcore_witness_owner'::regrole)))
AND (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='claimcore_witness'
  AND p.proname IN ('prepare_writer_handoff','commit_writer_handoff',
    'activate_writer_handoff','activate_data_use','abort_writer_handoff',
    'release_aborted_writer_handoff')
  AND p.prosecdef AND p.proowner::regrole::text='claimcore_witness_owner'
  AND p.proconfig = ARRAY['search_path=pg_catalog, claimcore_witness, pg_temp']
  AND NOT has_function_privilege(current_user,p.oid,'EXECUTE')
  AND NOT EXISTS (SELECT 1 FROM aclexplode(p.proacl) acl WHERE acl.grantee=0
    OR acl.grantee NOT IN ('claimcore_witness_owner'::regrole))) = 6
AND EXISTS (SELECT 1 FROM claimcore_witness.installation WHERE singleton
AND installation_id=@installation
AND lineage_id=@lineage
AND epoch=@epoch
AND baseline_id='claimcore-witness-v1'
AND baseline_sha256=@digest
AND ((tip_sequence=0
AND tip_hash=decode(repeat('00',32),'hex'))     OR (tip_sequence>0
AND EXISTS (SELECT 1 FROM claimcore_witness.journal j       WHERE j.installation_id=@installation
AND j.sequence=tip_sequence
AND j.entry_hash=tip_hash))))
