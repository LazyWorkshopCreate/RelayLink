# Docker Compose 部署

该方案用于 Linux Docker Engine。RelayLink Server 会根据通道配置动态监听业务端口，因此容器使用 host 网络；Docker Desktop 与非 Linux 容器环境不作为该方案的支持目标。

## 一键启动

需要 Docker Engine、Docker Compose v2 和 PowerShell 7：

```powershell
pwsh ./scripts/start-server-compose.ps1 -AgentServerHost tunnel.example.com
```

首次运行会：

1. 创建 `.local/docker-server/config/clients` 和 `.local/docker-server/data`。
2. 交互读取管理密码并只保存 PBKDF2-SHA256 哈希。
3. 生成 `.local/docker-server/config/server.json`。
4. 构建镜像并在临时容器中执行配置预检。
5. 预检通过后以后台方式启动服务。

再次执行会保留原配置和数据。默认管理页为 `http://127.0.0.1:18080/`，控制端口为 `7443`，数据端口为 `7444`。

### 启用 TLS

生成的入门配置没有启用隧道 TLS，只适合受控环境验证普通代理。生产使用或启用 Agent 互访前，必须把证书放入 `.local/docker-server/config/tls`，再修改 `server.json`：

```json
{
  "tunnel": {
    "tlsEnabled": true,
    "certificatePemPath": "/etc/relaylink/tls/fullchain.pem",
    "privateKeyPemPath": "/etc/relaylink/tls/privkey.pem",
    "trustedCaPemPath": "/etc/relaylink/tls/root-ca.pem"
  }
}
```

这里仅展示需调整的字段，不是完整配置。私钥、真实配置和 `.local` 数据不得提交到仓库。

## 常用命令

```powershell
# 查看状态和日志
docker compose -f deploy/docker/compose.yaml ps
docker compose -f deploy/docker/compose.yaml logs -f --tail 200

# 应用配置或镜像变更
pwsh ./scripts/start-server-compose.ps1

# 停止服务；保留配置和数据库
pwsh ./scripts/stop-server-compose.ps1
```

直接使用 Compose 时，先按 [server.docker.example.json](server.docker.example.json) 准备 `.local/docker-server/config/server.json` 和空的 `clients` 目录，然后运行：

```bash
docker compose -f deploy/docker/compose.yaml up -d --build
```

## 网络与数据

| 项目 | 默认值 | 说明 |
|---|---:|---|
| 管理页面 | `18080/tcp` | 示例监听全部主机网卡；应通过防火墙限制来源，或改绑 `127.0.0.1` |
| Agent 控制 | `7443/tcp` | 仅允许 Agent 来源访问 |
| Agent 数据 | `7444/tcp` | 仅允许 Agent 来源访问 |
| 普通通道 | 动态端口 | 由管理页中的通道配置决定，按需配置主机防火墙 |
| 配置和客户端 | `.local/docker-server/config` | 容器内路径 `/etc/relaylink`，可由管理页面更新客户端文件 |
| SQLite 数据 | `.local/docker-server/data` | 容器内路径 `/var/lib/relaylink`，备份时须包含 WAL 相关文件 |
| 服务端运行日志 | `.local/docker-server/data/logs` | 容器内 `/var/lib/relaylink/logs`，普通日志 14 天，错误日志永久保留 |

服务端使用 Serilog 将 JSONL 写入数据卷的 `logs/`，普通文件名为 `server-*.jsonl`，错误文件名为 `server-error-*.jsonl`，两类均按天或 10 MiB 轮转；普通日志保留 14 天，Error/Critical 不自动删除。Compose 对标准输出/错误仍启用单文件 10 MiB、最多 5 个文件的轮换，主要用于启动、配置预检及日志库写入失败诊断。停止容器不会删除配置、数据库和运行日志；不要在未备份时手工删除 `.local/docker-server`。

## 升级与排障

```powershell
git pull
pwsh ./scripts/start-server-compose.ps1
docker compose -f deploy/docker/compose.yaml logs --tail 200 relaylink-server
```

服务重建或重启会断开现有 Agent 和代理连接。若容器立即退出，优先检查日志以及 `.local/docker-server/config/server.json` 中的路径、端口冲突和 TLS 文件。host 网络下端口占用发生在 Docker 主机本身，可用主机工具检查监听状态。
