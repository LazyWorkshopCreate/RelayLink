#!/usr/bin/env node
// relaylink skill 包装脚本。
//
// 唯一职责：把参数原样转给 relaylink CLI，并返回它的 stdout / stderr / 退出码。
// 它不解析响应、不实现 TLS、不读写凭据、不合成任何协议细节。
// 前置要求：宿主机必须存在 Node.js 运行时。

import { spawn } from "node:child_process";

const argv = process.argv.slice(2);

if (argv.length === 0 || argv[0] === "--help" || argv[0] === "-h") {
  process.stdout.write(
    "用法: node relaylink.mjs <relaylink 子命令> [选项]\n" +
    "本脚本只转发参数。所有约束见 SKILL.md。\n"
  );
  process.exit(0);
}

const child = spawn(process.env.RELAYLINK_BIN || "relaylink", argv, {
  stdio: "inherit",
  // 不注入额外环境变量，避免把宿主秘密透传给 CLI。
  env: process.env
});

child.on("error", (error) => {
  if (error.code === "ENOENT") {
    process.stderr.write("找不到 relaylink 可执行程序。请先安装 CLI，或用 RELAYLINK_BIN 指定绝对路径。\n");
    process.exit(8);
  }
  process.stderr.write(`启动 relaylink 失败: ${error.message}\n`);
  process.exit(8);
});

child.on("exit", (code, signal) => {
  if (signal) {
    process.stderr.write(`relaylink 被信号终止: ${signal}\n`);
    process.exit(8);
  }
  // 原样透传退出码：agent 依赖 3/4/5/8/9 做判断。
  process.exit(code ?? 8);
});
