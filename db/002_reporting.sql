-- The reporting surface. The restricted login can SELECT from schema rpt and nothing else.
-- Every formula the answers use is here or in the compiler, written and reviewed by hand.

CREATE SCHEMA rpt;
GO

CREATE VIEW rpt.v_accounts AS
SELECT a.account_id, a.account_number, a.name AS account_name, a.account_type,
       a.household_id, h.name AS household, a.custodian_id, c.name AS custodian,
       a.model_id, m.name AS model, a.base_currency
FROM dbo.account AS a
JOIN dbo.custodian AS c ON c.custodian_id = a.custodian_id
JOIN dbo.model AS m ON m.model_id = a.model_id AND m.tenant_id = a.tenant_id
LEFT JOIN dbo.household AS h ON h.household_id = a.household_id AND h.tenant_id = a.tenant_id;
GO

CREATE VIEW rpt.v_households AS
SELECT household_id, name AS household FROM dbo.household;
GO

CREATE VIEW rpt.v_models AS
SELECT model_id, name AS model, kind FROM dbo.model;
GO

-- Custodians are shared reference data; only those holding an account visible in scope are listed.
CREATE VIEW rpt.v_custodians_in_scope AS
SELECT DISTINCT c.custodian_id, c.name AS custodian
FROM dbo.custodian AS c
JOIN dbo.account AS a ON a.custodian_id = c.custodian_id;
GO

CREATE VIEW rpt.v_securities AS
SELECT security_id, symbol, name AS security_name, asset_class, currency FROM dbo.security_master;
GO

CREATE VIEW rpt.v_security_tags AS
SELECT security_id, tag FROM dbo.security_tag;
GO

CREATE VIEW rpt.v_close_dates AS
SELECT DISTINCT price_date AS close_date FROM dbo.price;
GO

CREATE VIEW rpt.v_allocation_lines AS
SELECT al.allocation_id, al.block_id, b.side, b.trade_date, b.created_at,
       s.security_id, s.symbol, s.name AS security_name, s.asset_class,
       a.account_id, a.account_number, a.name AS account_name, a.household_id,
       a.custodian_id, c.name AS custodian,
       al.allocated_quantity, al.cancelled_quantity, al.cancelled_at
FROM dbo.allocation AS al
JOIN dbo.block_order AS b ON b.block_id = al.block_id AND b.tenant_id = al.tenant_id
JOIN dbo.security_master AS s ON s.security_id = b.security_id
JOIN dbo.account AS a ON a.account_id = al.account_id AND a.tenant_id = al.tenant_id
JOIN dbo.custodian AS c ON c.custodian_id = a.custodian_id;
GO

CREATE VIEW rpt.v_executions AS
SELECT execution_id, allocation_id, quantity, price, executed_at FROM dbo.execution;
GO

-- Positions on a trade-date or settlement-date basis, valued in USD at the as-of close.
-- A position without a close or without an FX rate keeps a NULL value and says why.
CREATE FUNCTION rpt.fn_positions (@as_of date, @basis varchar(10))
RETURNS TABLE
AS RETURN
WITH q AS (
    SELECT o.account_id, o.security_id, o.quantity
    FROM dbo.opening_position AS o
    WHERE o.as_of <= @as_of
    UNION ALL
    SELECT t.account_id, t.security_id, t.quantity
    FROM dbo.txn AS t
    WHERE CASE WHEN @basis = 'settlement' THEN t.settle_date ELSE t.trade_date END <= @as_of
), h AS (
    SELECT account_id, security_id, SUM(quantity) AS quantity
    FROM q
    GROUP BY account_id, security_id
    HAVING SUM(quantity) <> 0
)
SELECT h.account_id, h.security_id, s.symbol, s.name AS security_name, s.asset_class, s.currency,
       h.quantity, p.close_price, fx.usd_per_unit,
       CAST(h.quantity AS decimal(18,4)) * CAST(p.close_price AS decimal(18,6)) * CAST(fx.usd_per_unit AS decimal(12,8)) AS market_value_usd,
       CASE WHEN p.close_price IS NULL THEN CONCAT(N'no close for ', s.symbol, N' on ', CONVERT(char(10), @as_of, 23))
            WHEN fx.usd_per_unit IS NULL THEN CONCAT(N'no ', s.currency, N'/USD rate on ', CONVERT(char(10), @as_of, 23))
       END AS missing_reason
FROM h
JOIN dbo.security_master AS s ON s.security_id = h.security_id
LEFT JOIN dbo.price AS p ON p.security_id = h.security_id AND p.price_date = @as_of
LEFT JOIN dbo.fx_rate AS fx ON fx.currency = s.currency AND fx.rate_date = @as_of;
GO

-- Security-level targets of each account's model, multiplying weights down the hierarchy (any depth up to 10).
CREATE FUNCTION rpt.fn_account_targets ()
RETURNS TABLE
AS RETURN
WITH walk AS (
    SELECT a.account_id, mc.child_model_id, mc.security_id, CAST(mc.weight AS decimal(28,12)) AS weight, 1 AS depth
    FROM dbo.account AS a
    JOIN dbo.model_component AS mc ON mc.model_id = a.model_id AND mc.tenant_id = a.tenant_id
    UNION ALL
    SELECT w.account_id, mc.child_model_id, mc.security_id, CAST(w.weight * mc.weight AS decimal(28,12)), w.depth + 1
    FROM walk AS w
    JOIN dbo.model_component AS mc ON mc.model_id = w.child_model_id
    WHERE w.depth < 10
)
SELECT account_id, security_id, SUM(weight) AS target_weight
FROM walk
WHERE security_id IS NOT NULL
GROUP BY account_id, security_id;
GO
