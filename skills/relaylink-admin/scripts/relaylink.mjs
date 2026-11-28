#!/usr/bin/env node
// relaylink skill 包装脚本。
//
// 唯一职责：把参数原样转给 relaylink CLI，并返回它的 stdout / stderr / 退出码。
// 它不解析响应、不实现 TLS、不读写凭据、不合成任何协议细节。
// 前置要求：宿主机必须存在 Node.js 运行时。

import { spawn } from "node:child_process";
import path from "node:path";

const argv = process.argv.slice(2);

if (argv.length === 0 || argv[0] === "--help" || argv[0] === "-h") {
  process.stdout.write(
    "用法: node relaylink.mjs <relaylink 子命令> [选项]\n" +
    "本脚本只转发参数。所有约束见 SKILL.md。\n"
  );
  process.exit(0);
}

const explicitBin = process.env.RELAYLINK_BIN;
if (explicitBin && !path.isAbsolute(explicitBin)) {
  process.stderr.write("RELAYLINK_BIN 必须是绝对路径。\n");
  process.exit(8);
}

const ALLOWED_ENV = [
  "PATH",
  "HOME",
  "USERPROFILE",
  "APPDATA",
  "LOCALAPPDATA",
  "XDG_CONFIG_HOME",
  "XDG_DATA_HOME",
  "SYSTEMROOT",
  "COMSPEC",
  "TEMP",
  "TMP",
  "TMPDIR",
  "LANG",
  "LC_ALL",
  "RELAYLINK_HOME",
  "DOTNET_ROOT",
  "PATHEXT"
];

const cleanEnv = {};
for (const key of ALLOWED_ENV) {
  if (typeof process.env[key] === "string") {
    cleanEnv[key] = process.env[key];
  }
}

const child = spawn(explicitBin || "relaylink", argv, {
  stdio: "inherit",
  env: cleanEnv
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
