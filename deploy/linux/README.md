# Linux 部署

## Server

先在构建机运行 `scripts/publish.ps1 -RuntimeIdentifier linux-x64 -Component Server`，再将 Linux 发布产物放入 `/opt/relaylink/server`。将服务端配置、客户端目录与 TLS 私钥放入 `/etc/relaylink`。使用 `relaylink` 专用账号，并确保该账号只能读取必要配置和证书私钥。

部署前执行：

```bash
/opt/relaylink/server/RelayLink.Server --config /etc/relaylink/server.json --check-config
sudo install -m 0644 deploy/linux/relaylink-server.service /etc/systemd/system/relaylink-server.service
sudo systemctl daemon-reload
sudo systemctl enable --now relaylink-server
```

服务重启会断开所有控制会话和现有代理连接。Agent 必须能访问控制端口 `tunnel.port` 和独立数据端口 `tunnel.dataPort`（缺省为控制端口加一）；代理端口和仪表盘端口只应在内网可达。普通数据端口目前不加密，开放范围应限于 Agent 来源，并由受信隔离链路或网络层加密保护；安全组不提供保密性。升级时 Server 与 Agent 必须一起更新，旧版本不能混用。

## Agent

Linux Agent 使用与 Windows Agent 相同的配置、协议、互访通道和本机只读状态页。先构建或下载 `linux-x64` 自包含发布包：

```bash
pwsh ./scripts/publish.ps1 -RuntimeIdentifier linux-x64 -Component Agent
```

建议使用独立的无登录服务账号。Agent 会在配置文件所在目录创建端到端身份、端口状态和诊断日志，因此该目录必须仅允许服务账号读写；程序目录由 root 管理且不允许服务账号写入。

```bash
sudo useradd --system --home-dir /var/lib/relaylink-agent --shell /usr/sbin/nologin relaylink-agent
sudo install -d -o root -g root -m 0755 /opt/relaylink/agent
sudo tar -xzf RelayLink-Agent-linux-x64-<version>.tar.gz -C /opt/relaylink/agent
sudo chown -R root:root /opt/relaylink/agent
sudo chmod 0755 /opt/relaylink/agent/RelayLink.Agent
sudo install -d -o relaylink-agent -g relaylink-agent -m 0700 /var/lib/relaylink-agent
sudo install -o relaylink-agent -g relaylink-agent -m 0600 ./relaylink-agent-<clientId>.json /var/lib/relaylink-agent/agent.json
sudo -u relaylink-agent /opt/relaylink/agent/RelayLink.Agent --config /var/lib/relaylink-agent/agent.json --check-config
sudo install -m 0644 deploy/linux/relaylink-agent.service /etc/systemd/system/relaylink-agent.service
sudo systemctl daemon-reload
sudo systemctl enable --now relaylink-agent
```

通过 `systemctl status relaylink-agent` 和 `journalctl -u relaylink-agent` 检查进程；本机状态页默认位于 `http://127.0.0.1:18081/`。升级时只替换 `/opt/relaylink/agent` 中的程序文件并重启服务，不要覆盖 `/var/lib/relaylink-agent` 中的配置和身份状态。状态目录不放在 `/var/lib/relaylink/` 下，避免被 Server 服务账号目录的遍历权限阻断。

## 通过互访通道访问服务端管理页

无需增加管理页面专用协议。可以在 RelayLink Server 所在 Linux 主机同时运行一个标准 Linux Agent，并按普通授权互访流程配置：

1. 将 Server 的 `dashboard.listenAddress` 设为 `127.0.0.1`、端口设为 `18080`，避免管理页直接暴露到外部网络。
2. 为同机 Linux Agent 创建一个启用的授权互访通道，目标为 `127.0.0.1:18080`；例如通道 ID 使用 `server-dashboard`，并打开“仅允许授权客户端互访”。
3. 为获准访问的另一客户端创建指向该 Linux Agent 与 `server-dashboard` 通道的端到端访问入口。
4. 在访问方 Agent 本机状态页读取实际入口地址，并在同机浏览器打开 `http://127.0.0.1:<入口端口>/`。

浏览器到访问方 Agent 仅走本机 loopback；两个 Agent 之间沿用互访内层 TLS、目标证书指纹固定和访问密钥证明；Linux Agent 到管理页只走服务端本机 loopback。不要把访问方入口改绑为 LAN 或公网地址。若同机 Agent 使用服务端 TLS 域名连接控制入口，应确保该域名在本机解析到可达地址且仍与证书 SAN 匹配；不要为了绕过名称校验把 `serverHost` 改成证书不包含的 `127.0.0.1`。
