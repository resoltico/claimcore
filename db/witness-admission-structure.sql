SELECT true
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
AND has_table_privilege(current_user,'claimcore_witness.installation_loss_retirements','SELECT')
AND NOT has_table_privilege(current_user,'claimcore_witness.installation_loss_retirements','INSERT')
AND NOT has_table_privilege(current_user,'claimcore_witness.installation_loss_retirements','UPDATE')
AND NOT has_table_privilege(current_user,'claimcore_witness.installation_loss_retirements','DELETE')
AND NOT has_table_privilege(current_user,'claimcore_witness.writer_handoffs','INSERT')
AND NOT has_table_privilege(current_user,'claimcore_witness.writer_handoffs','UPDATE')
AND NOT has_table_privilege(current_user,'claimcore_witness.writer_handoffs','DELETE')
AND NOT has_table_privilege(current_user,'claimcore_witness.writer_handoffs','TRUNCATE')
AND has_function_privilege(current_user,'claimcore_witness.append(claimcore_witness.installation_identity,claimcore_witness.journal_subject,claimcore_witness.journal_request,bytea)','EXECUTE')
AND has_function_privilege(current_user,'claimcore_witness.acquire_read_fence(claimcore_witness.installation_identity,bytea)','EXECUTE')
AND (SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname='claimcore_witness') = 'claimcore_witness_owner'
AND (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace   WHERE n.nspname='claimcore_witness') = 43
AND (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace   WHERE n.nspname='claimcore_witness') = 81
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
AND NOT t.tgisinternal) = 2
AND NOT pg_has_role('claimcore_witness_writer','claimcore_witness_owner','MEMBER')
AND NOT pg_has_role('claimcore_witness_auditor','claimcore_witness_owner','MEMBER')
AND NOT EXISTS (
  SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='claimcore_witness' AND (
    p.proowner::regrole::text<>'claimcore_witness_owner'
    OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog, claimcore_witness, pg_temp']
    OR p.prosecdef<>(p.proname IN ('guard_loss_journal','guard_loss_installation',
      'acquire_read_fence','append','prepare_writer_handoff','commit_writer_handoff',
      'activate_writer_handoff','activate_data_use','abort_writer_handoff',
      'release_aborted_writer_handoff','settle_and_prune','rotate_key',
      'prepare_installation_loss_retirement','settle_installation_loss_retirement'))
    OR has_function_privilege(current_user,p.oid,'EXECUTE')<>(p.proname IN ('append','acquire_read_fence'))
    OR EXISTS (SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) acl
      WHERE acl.grantee NOT IN ('claimcore_witness_owner'::regrole,
        CASE WHEN p.proname IN ('append','acquire_read_fence')
          THEN 'claimcore_witness_writer'::regrole ELSE 'claimcore_witness_owner'::regrole END))
  )
)
AND (SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace
  WHERE n.nspname='claimcore_witness' AND t.typtype='c')=27
AND NOT EXISTS (
  SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace
  WHERE n.nspname='claimcore_witness' AND t.typtype='c' AND (
    t.typowner::regrole::text<>'claimcore_witness_owner'
    OR has_type_privilege(current_user,t.oid,'USAGE')<>(t.typname IN
      ('installation_identity','journal_subject','journal_request'))
    OR EXISTS (SELECT 1 FROM aclexplode(coalesce(t.typacl,acldefault('T',t.typowner))) acl
      WHERE acl.grantee NOT IN ('claimcore_witness_owner'::regrole,
        CASE WHEN t.typname IN ('installation_identity','journal_subject','journal_request')
          THEN 'claimcore_witness_writer'::regrole ELSE 'claimcore_witness_owner'::regrole END))
  )
)
