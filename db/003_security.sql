-- Tenant isolation enforced by the database, not by the prompt.
-- The application pins tenant_id with sp_set_session_context ... @read_only = 1 after authenticating the user.
-- With no tenant in the session, reading a tenant table raises an error instead of returning an empty answer.

CREATE SCHEMA sec;
GO

CREATE FUNCTION sec.fn_tenant_predicate (@tenant_id int)
RETURNS TABLE
WITH SCHEMABINDING
AS RETURN
SELECT 1 AS allowed
WHERE CASE
        WHEN USER_NAME() = N'dbo' THEN 1                                   -- schema owner: setup and seeding only
        WHEN SESSION_CONTEXT(N'tenant_id') IS NULL
             THEN CAST(N'tenant scope missing: the session has no tenant_id' AS int)  -- fails the query
        WHEN @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS int) THEN 1
        ELSE 0
      END = 1;
GO

CREATE SECURITY POLICY sec.tenant_isolation
    ADD FILTER PREDICATE sec.fn_tenant_predicate(tenant_id) ON dbo.household,
    ADD FILTER PREDICATE sec.fn_tenant_predicate(tenant_id) ON dbo.model,
    ADD FILTER PREDICATE sec.fn_tenant_predicate(tenant_id) ON dbo.model_component,
    ADD FILTER PREDICATE sec.fn_tenant_predicate(tenant_id) ON dbo.account,
    ADD FILTER PREDICATE sec.fn_tenant_predicate(tenant_id) ON dbo.opening_position,
    ADD FILTER PREDICATE sec.fn_tenant_predicate(tenant_id) ON dbo.block_order,
    ADD FILTER PREDICATE sec.fn_tenant_predicate(tenant_id) ON dbo.allocation,
    ADD FILTER PREDICATE sec.fn_tenant_predicate(tenant_id) ON dbo.execution,
    ADD FILTER PREDICATE sec.fn_tenant_predicate(tenant_id) ON dbo.txn,
    ADD BLOCK PREDICATE sec.fn_tenant_predicate(tenant_id) ON dbo.account AFTER INSERT,
    ADD BLOCK PREDICATE sec.fn_tenant_predicate(tenant_id) ON dbo.allocation AFTER INSERT
WITH (STATE = ON, SCHEMABINDING = ON);
GO

-- The reporting role: read the rpt schema, nothing else. No access to dbo, no DDL, no writes.
CREATE ROLE nlq_reporting;
GRANT SELECT ON SCHEMA::rpt TO nlq_reporting;
GO
