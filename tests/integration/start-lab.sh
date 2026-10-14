#!/usr/bin/env bash
# Starts the two SQL Server 2025 containers used by tests/Vha.Infrastructure.Tests ("node A" and "node B").
# Both mount /tmp/vhashared at the same path, so the directory plays the role of the VMDK disks that move between the nodes.
set -euo pipefail
IMAGE="${VHA_SQL_IMAGE:-mcr.microsoft.com/mssql/server:2025-latest}"
PASSWORD="Vha_Test#2026x"          # must match Lab.Password in Lab.cs
mkdir -p /tmp/vhashared && chmod 777 /tmp/vhashared
for n in A B; do
  port=$([ "$n" = A ] && echo 14331 || echo 14332)
  docker rm -f "sql$n" >/dev/null 2>&1 || true
  docker run -d --name "sql$n" -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$PASSWORD" -e MSSQL_PID=Developer \
    -p "$port:1433" -v /tmp/vhashared:/tmp/vhashared "$IMAGE" >/dev/null
done
echo "waiting for SQL Server..."
for n in A B; do
  for i in $(seq 1 60); do
    # 2022/2025 images ship mssql-tools18 (needs -C), 2017/2019 ship mssql-tools
    if docker exec "sql$n" sh -c "if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P '$PASSWORD' -Q 'SELECT 1'; else /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P '$PASSWORD' -Q 'SELECT 1'; fi" >/dev/null 2>&1; then echo "sql$n ready"; break; fi
    sleep 2
  done
done
