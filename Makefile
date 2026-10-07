# One command each. `make test` = verify + sql.
SHELL := /bin/bash
DOTNET ?= dotnet
NLQ := $(DOTNET) run --project src/PortfolioNlq.Cli --no-build --
PUBLISHED_RECORDING := $(word 1,$(shell cat runs/PUBLISHED))
PUBLISHED_RUN := $(word 2,$(shell cat runs/PUBLISHED))
PORT ?= 14333
RUN_ID ?= local-$(shell date +%Y%m%d-%H%M%S)

.PHONY: build verify sql test record expected clean-sql

build:
	$(DOTNET) build PortfolioNlq.slnx -v quiet -nologo

## No database, no Docker, no key: fixture and expected results, recorded replies -> exact SQL, stored rows re-graded, unit tests.
verify: build
	$(DOTNET) test tests/PortfolioNlq.Tests --no-build -v quiet -nologo
	$(NLQ) verify --recording $(PUBLISHED_RECORDING) --run $(PUBLISHED_RUN)

.env:
	@echo "MSSQL_SA_PASSWORD=Sa-$$(openssl rand -hex 12)-1!" > .env
	@echo "NLQ_READER_PASSWORD=Rd-$$(openssl rand -hex 12)-1!" >> .env
	@echo "generated .env with throwaway local passwords (git-ignored)"

## SQL Server in Docker (Linux x86-64): security tests under the restricted login, then the recorded run replayed through the real .NET path.
sql: build .env
	set -a; source .env; set +a; \
	NLQ_SQL_PORT=$(PORT) docker compose up -d --wait && \
	export NLQ_ADMIN_CONNECTION="Server=localhost,$(PORT);User ID=sa;Password=$$MSSQL_SA_PASSWORD;TrustServerCertificate=True;Encrypt=True" && \
	export NLQ_READER_CONNECTION="Server=localhost,$(PORT);Database=NlqReference;User ID=nlq_reader;Password=$$NLQ_READER_PASSWORD;TrustServerCertificate=True;Encrypt=True" && \
	$(NLQ) db-setup && \
	$(DOTNET) test tests/PortfolioNlq.SqlTests --no-build -nologo --logger "console;verbosity=normal" && \
	$(NLQ) replay --recording $(PUBLISHED_RECORDING) --run-id $(RUN_ID) --out runs/local-latest --sql && \
	$(NLQ) compare --a $(PUBLISHED_RUN) --b runs/local-latest

test: verify sql

## Re-record model replies with `claude -p` (needs Claude Code logged in; runs on the subscription, no API key).
record: build
	$(NLQ) record --out recordings/rec-$$(date +%Y-%m-%d-%H%M) --model sonnet

## Recompute the independent expected results (only when the fixture or the cases change).
expected: build
	$(NLQ) expected

clean-sql:
	docker compose down -v
