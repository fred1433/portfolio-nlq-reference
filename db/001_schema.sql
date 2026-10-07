-- Tables. Every tenant-owned row carries tenant_id, and composite foreign keys make a row
-- unable to point at a parent that belongs to another tenant.

CREATE TABLE dbo.tenant (
    tenant_id          int           NOT NULL PRIMARY KEY,
    name               nvarchar(100) NOT NULL,
    reporting_currency char(3)       NOT NULL
);

CREATE TABLE dbo.custodian (
    custodian_id int           NOT NULL PRIMARY KEY,
    name         nvarchar(100) NOT NULL UNIQUE
);

CREATE TABLE dbo.security_master (
    security_id int           NOT NULL PRIMARY KEY,
    symbol      varchar(12)   NOT NULL UNIQUE,
    name        nvarchar(200) NOT NULL,
    asset_class nvarchar(40)  NOT NULL,
    currency    char(3)       NOT NULL
);

CREATE TABLE dbo.security_tag (
    security_id int          NOT NULL REFERENCES dbo.security_master (security_id),
    tag         nvarchar(60) NOT NULL,
    PRIMARY KEY (security_id, tag)
);

-- A missing close is a missing row, never a zero.
CREATE TABLE dbo.price (
    security_id int           NOT NULL REFERENCES dbo.security_master (security_id),
    price_date  date          NOT NULL,
    close_price decimal(18,6) NOT NULL CHECK (close_price > 0),
    PRIMARY KEY (security_id, price_date)
);

CREATE TABLE dbo.fx_rate (
    currency     char(3)       NOT NULL,
    rate_date    date          NOT NULL,
    usd_per_unit decimal(12,8) NOT NULL CHECK (usd_per_unit > 0),
    PRIMARY KEY (currency, rate_date)
);

CREATE TABLE dbo.household (
    household_id int           NOT NULL PRIMARY KEY,
    tenant_id    int           NOT NULL REFERENCES dbo.tenant (tenant_id),
    name         nvarchar(200) NOT NULL,
    UNIQUE (tenant_id, household_id)
);

CREATE TABLE dbo.model (
    model_id  int           NOT NULL PRIMARY KEY,
    tenant_id int           NOT NULL REFERENCES dbo.tenant (tenant_id),
    name      nvarchar(200) NOT NULL,
    kind      nvarchar(20)  NOT NULL,
    UNIQUE (tenant_id, model_id)
);

-- A component is either a child model (a sleeve) or a security, never both.
CREATE TABLE dbo.model_component (
    component_id   int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    tenant_id      int          NOT NULL,
    model_id       int          NOT NULL,
    child_model_id int          NULL,
    security_id    int          NULL REFERENCES dbo.security_master (security_id),
    weight         decimal(9,6) NOT NULL CHECK (weight > 0 AND weight <= 1),
    FOREIGN KEY (tenant_id, model_id)       REFERENCES dbo.model (tenant_id, model_id),
    FOREIGN KEY (tenant_id, child_model_id) REFERENCES dbo.model (tenant_id, model_id),
    CHECK ((child_model_id IS NULL AND security_id IS NOT NULL) OR (child_model_id IS NOT NULL AND security_id IS NULL))
);

CREATE TABLE dbo.account (
    account_id     int           NOT NULL PRIMARY KEY,
    tenant_id      int           NOT NULL REFERENCES dbo.tenant (tenant_id),
    account_number varchar(20)   NOT NULL,
    name           nvarchar(200) NOT NULL,
    account_type   nvarchar(40)  NOT NULL,
    household_id   int           NULL,
    custodian_id   int           NOT NULL REFERENCES dbo.custodian (custodian_id),
    model_id       int           NOT NULL,
    base_currency  char(3)       NOT NULL,
    UNIQUE (tenant_id, account_id),
    UNIQUE (tenant_id, account_number),
    FOREIGN KEY (tenant_id, household_id) REFERENCES dbo.household (tenant_id, household_id),
    FOREIGN KEY (tenant_id, model_id)     REFERENCES dbo.model (tenant_id, model_id)
);

-- Settled book at the opening date. Transactions after it are in dbo.txn.
CREATE TABLE dbo.opening_position (
    tenant_id   int           NOT NULL,
    account_id  int           NOT NULL,
    security_id int           NOT NULL REFERENCES dbo.security_master (security_id),
    quantity    decimal(18,4) NOT NULL,
    as_of       date          NOT NULL,
    PRIMARY KEY (account_id, security_id, as_of),
    FOREIGN KEY (tenant_id, account_id) REFERENCES dbo.account (tenant_id, account_id)
);

CREATE TABLE dbo.block_order (
    block_id       int           NOT NULL PRIMARY KEY,
    tenant_id      int           NOT NULL REFERENCES dbo.tenant (tenant_id),
    security_id    int           NOT NULL REFERENCES dbo.security_master (security_id),
    side           varchar(4)    NOT NULL CHECK (side IN ('Buy', 'Sell')),
    order_quantity decimal(18,4) NOT NULL CHECK (order_quantity > 0),
    trade_date     date          NOT NULL,
    created_at     datetime2(0)  NOT NULL,
    status         nvarchar(30)  NOT NULL,
    UNIQUE (tenant_id, block_id)
);

-- Allocations of a block do not have to add up to the block quantity.
CREATE TABLE dbo.allocation (
    allocation_id      int           NOT NULL PRIMARY KEY,
    tenant_id          int           NOT NULL,
    block_id           int           NOT NULL,
    account_id         int           NOT NULL,
    allocated_quantity decimal(18,4) NOT NULL CHECK (allocated_quantity > 0),
    cancelled_quantity decimal(18,4) NOT NULL DEFAULT 0 CHECK (cancelled_quantity >= 0),
    cancelled_at       datetime2(0)  NULL,
    UNIQUE (tenant_id, allocation_id),
    FOREIGN KEY (tenant_id, block_id)   REFERENCES dbo.block_order (tenant_id, block_id),
    FOREIGN KEY (tenant_id, account_id) REFERENCES dbo.account (tenant_id, account_id)
);

CREATE TABLE dbo.execution (
    execution_id  int           NOT NULL PRIMARY KEY,
    tenant_id     int           NOT NULL,
    allocation_id int           NOT NULL,
    quantity      decimal(18,4) NOT NULL CHECK (quantity > 0),
    price         decimal(18,6) NOT NULL CHECK (price > 0),
    executed_at   datetime2(0)  NOT NULL,
    UNIQUE (tenant_id, execution_id),
    FOREIGN KEY (tenant_id, allocation_id) REFERENCES dbo.allocation (tenant_id, allocation_id)
);

-- Trade date and settlement date are both kept; positions can be read on either basis.
CREATE TABLE dbo.txn (
    transaction_id int           NOT NULL PRIMARY KEY,
    tenant_id      int           NOT NULL,
    account_id     int           NOT NULL,
    security_id    int           NOT NULL REFERENCES dbo.security_master (security_id),
    quantity       decimal(18,4) NOT NULL,
    trade_date     date          NOT NULL,
    settle_date    date          NOT NULL,
    type           nvarchar(30)  NOT NULL,
    execution_id   int           NULL,
    CHECK (settle_date >= trade_date),
    FOREIGN KEY (tenant_id, account_id)   REFERENCES dbo.account (tenant_id, account_id),
    FOREIGN KEY (tenant_id, execution_id) REFERENCES dbo.execution (tenant_id, execution_id)
);
