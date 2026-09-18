# Linux 服务端部署

先在构建机运行 `scripts/publish.ps1 -RuntimeIdentifier linux-x64 -Component Server`，再将 Linux 发布产物放入 `/opt/relaylink/server`。将服务端配置、客户端目录与 TLS 私钥放入 `/etc/relaylink`。使用 `relaylink` 专用账号，并确保该账号只能读取必要配置和证书私钥。

部署前执行：

```bash
/opt/relaylink/server/RelayLink.Server --config /etc/relaylink/server.json --check-config
sudo install -m 0644 deploy/linux/relaylink-server.service /etc/systemd/system/relaylink-server.service
sudo systemctl daemon-reload
sudo systemctl enable --now relaylink-server
```

服务重启会断开所有控制会话和现有代理连接。安全组仅应向公网开放隧道端口；代理端口和仪表盘端口只应在内网可达。
