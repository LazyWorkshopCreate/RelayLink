# Agent 配置内嵌受信 CA

文档 ID：ADR-0007\
状态：Accepted\
版本：v1.0.0\
更新日期：2026-09-17

## 背景

此前管理端创建客户端时要求填写 Agent 本机的 CA 文件路径。这个路径取决于目标 Windows 主机的安装位置，服务端既无法验证，也不应由管理员在创建客户端时猜测。下载的配置因此不能独立安装。

## 决定

- 服务端基础配置的 `tunnel.trustedCaPemPath` 指向仅含 CA 公钥证书的 PEM。启用 TLS 时，创建客户端及下载 Agent 配置要求服务端配置该路径。
- 下载的 Agent JSON 将 PEM 的 UTF-8 字节以 `trustedCaPemBase64` 内嵌，不包含服务端私钥；新建的每客户端配置不保存 CA 路径，旧文件中的路径字段仅为读取兼容而保留且不再用于下载。
- Agent 解码并校验 CA 后用自定义根信任链验证服务端 TLS 证书及主机名，不使用操作系统根证书库。旧 `trustedCaPemPath` 继续只读兼容，但与新字段不能同时存在。
- CA 轮换需更新服务端 PEM，并重新下载和部署 Agent 配置；现有 Agent 不热更新信任锚。

## 备选

- 继续让管理员填写 Agent 路径：无法跨主机保证路径有效，拒绝。
- 从服务端证书自动信任其颁发者：证书链不一定携带可信根，且信任锚选择不应隐式完成，拒绝。

## 后果

单个 JSON 即可完成 Agent 安装，但配置内含独立客户端密钥和 CA 公钥证书，仍须按敏感配置保护。旧安装包不认识新字段，使用新下载配置时必须升级 Agent/安装包。服务端必须保护其 CA 文件，且不得把私钥置于该路径。

关联：[需求](../requirements/requirements.md)、[技术设计](../design/technical-design.md)、[安装与使用](../operations/installation-and-usage.md)。
