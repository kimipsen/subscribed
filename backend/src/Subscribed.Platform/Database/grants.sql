-- Single source of truth for the application role's per-object privileges (design §10.3).
-- Applied by `admin db migrate` (as the migrator, which owns the schema) and by the test fixture.
-- Tokens __SCHEMA__ and __APP_ROLE__ are substituted only after both match ^[a-z_][a-z0-9_]{0,62}$.
--
-- Derivation (B-1 / E5, backend/README.md): Marten 9.59.0, Rich append mode, string stream ids,
-- inline household view projection, DP key documents, on PostgreSQL 18. Each GRANT line below was
-- shown NECESSARY (removing it makes a scenario fail with 42501) and the set SUFFICIENT (every
-- scenario passes as the app role). Readiness (/health/ready) verifies every GRANT line below and
-- the absence of DELETE/TRUNCATE/REFERENCES/TRIGGER anywhere, UPDATE on mt_events and CREATE on the
-- schema. Keep exactly one statement per line: the readiness check parses the GRANT lines.

-- Reset to an exact state first so that re-applying never accumulates privileges.
REVOKE ALL ON ALL TABLES IN SCHEMA __SCHEMA__ FROM __APP_ROLE__;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA __SCHEMA__ FROM __APP_ROLE__;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA __SCHEMA__ FROM __APP_ROLE__;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA __SCHEMA__ FROM PUBLIC;
REVOKE CREATE ON SCHEMA __SCHEMA__ FROM __APP_ROLE__;

GRANT USAGE ON SCHEMA __SCHEMA__ TO __APP_ROLE__;
GRANT SELECT, INSERT ON TABLE __SCHEMA__.mt_events TO __APP_ROLE__;
GRANT SELECT, INSERT, UPDATE ON TABLE __SCHEMA__.mt_streams TO __APP_ROLE__;
GRANT USAGE ON SEQUENCE __SCHEMA__.mt_events_sequence TO __APP_ROLE__;
GRANT SELECT, INSERT, UPDATE ON TABLE __SCHEMA__.mt_doc_household_view TO __APP_ROLE__;
GRANT SELECT, INSERT ON TABLE __SCHEMA__.mt_doc_dp_key TO __APP_ROLE__;
GRANT SELECT ON TABLE __SCHEMA__.schema_migrations TO __APP_ROLE__;
