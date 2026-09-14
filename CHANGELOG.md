# Changelog

## Unreleased

## 0.11.0 - 2026-09-13

### Features

- 首个发布：原生 .NET 8+ Brick runtime SDK，直接实现 `brickly.runtime.v1`（loopback gRPC `invoke` / `interact`），不包装 Go DLL。
- 内嵌 Kestrel gRPC server（`127.0.0.1:0`），Host 拨入 `BrickCommandService`；缺 `BRICKLY_HOST_ENDPOINT` 时 `StartAsync` 抛 `PROTOCOL_ERROR`。
- 命令与依赖：`OnCommand`、`InvokeAsync` / `InteractAsync` / `CallAsync`、`Dependencies.Require(alias)`；命令内 `StartAsync` 占用跟这次 Call，命令外 / 事件 scope 报 `PARENT_INVOCATION_REQUIRED`。
- 窗口：`UI.CreateBrowserWindowAsync`（Session 绑定，`keepAlive` 默认 `false`）；`webContents.send` 命令外必须带 `requestId`；`window.closed` 去重且 Runtime 结束释放全部句柄。
- 资源：`OpenResource`（惰性）、`CreateResourceAsync`（超过 1 MiB 自动走 Writer）、`CreateResourceFromAsync`、`CreateResourceWriterAsync`。
- 平台能力：`Platform.*` / `System.*`、`Events.On` / `Events.PublishAsync`、`Storage`（KV / collection / secrets / watch）、`Config`。
- 生成绑定由 `buf.gen.yaml` 的 csharp 插件输出，`npm run check:runtime-proto` 做漂移门禁。
