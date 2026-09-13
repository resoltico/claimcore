-- Let the confined runtime read pre-003 uncertainty evidence without mutating it.
GRANT SELECT ON claimcore.request_submission_legacy_uncertainty TO claimcore_app;
