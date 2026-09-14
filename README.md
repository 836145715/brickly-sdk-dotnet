# Syllm.Brickly.Sdk（.NET 8+）

Brickly Brick **原生 .NET runtime** SDK。生产协议为 `brickly.runtime.v1`（loopback gRPC `invoke` / `interact`），语义与 Node / Go / Python SDK 对齐。

- **原生 Follower**：C# 直接实现协议，不包装 Go DLL。
- **内嵌 gRPC server**：Host 会主动拨入 Brick 进程的 `BrickCommandService`，SDK 用 Kestrel 在 `127.0.0.1:0` 起 HTTP/2 明文服务。
- **framework-dependent**：用户机器需要 **.NET 8 运行时（含 ASP.NET Core）**，下载 <https://dotnet.microsoft.com/download/dotnet/8.0>（选 "ASP.NET Core Runtime"）。宿主要拉起 Brick 前可用 `dotnet --list-runtimes` 预检。

---

## 快速上手

```csharp
using System.Text.Json;
using Syllm.Brickly.Sdk;

var runtime = new BricklyRuntime();

runtime.OnCommand("hello", (ctx, input) =>
{
    var name = input.TryGetProperty("name", out var value) ? value.GetString() : null;
    ctx.Info("hello", new Dictionary<string, object?> { ["name"] = name });
    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
        ["message"] = "Hello, " + (name ?? "Brickly"),
    });
});

await runtime.StartAsync();        // 缺 BRICKLY_HOST_ENDPOINT 会抛 PROTOCOL_ERROR
await runtime.WaitForShutdownAsync();
```

`StartAsync` 完成注册后返回；`WaitForShutdownAsync`（或 `DisposeAsync`）负责进程存活与退出清理。

SDK 自动完成：

- 连接 `BRICKLY_HOST_ENDPOINT` 并注册 gRPC Runtime
- `invoke` / `interact` 命令分发
- Host 平台 / Resource / Event / Connector / Storage 客户端路由
- `CancellationToken` 取消信号
- `OnShutdown` 钩子

---

## 核心 API

### `BricklyRuntime`（`new BricklyRuntime()`）

| 成员 | 作用 |
| --- | --- |
| `OnCommand(id, handler)` | 注册命令处理器（链式） |
| `InvokeAsync(commandId, input, ct)` | 再跑自己的一条命令；已有占用则不 Dispose |
| `InteractAsync(commandId, input, opts, ct)` | 已有占用上再开会话；必须传 `OnEvent` |
| `CallAsync(commandId, input, opts, ct)` | Interact + 半关闭的糖；与命令 `mode=call` 对齐 |
| `OnReady(fn)` / `OnShutdown(fn)` | 注册 / 关闭钩子 |
| `UI.CreateBrowserWindowAsync(url, opts)` | 创建 Session 子窗口 |
| `UI.ListWindowsAsync()` | 列出本 Brick 持有的窗口 |
| `Events.On(event, fn)` | 订阅公共事件（`命名空间:主题`），返回 `IDisposable` |
| `Events.PublishAsync(event, payload)` | 发布事件 |
| `Platform.*` / `System.*` | 宿主系统能力 |
| `Dependencies.Require(alias)` | 获取 Host 握手绑定的依赖客户端 |
| `OpenResource(ref)` | 惰性绑定已有 `ResourceRef` |
| `CreateResourceAsync(content, options)` | 创建资源；超过 1 MiB 自动走 Writer |
| `CreateResourceFromAsync(stream, options)` | 从 `Stream` 流式创建 |
| `CreateResourceWriterAsync(options)` | 多次 `WriteAsync`，Finish 后返回 Handle |
| `StartAsync(ct)` / `WaitForShutdownAsync()` | 启动 / 等待退出 |
| `Debug/Info/Warn/Error` | 经 Host `diagnostics.log` 进入日志中心 |

### `CommandContext`

| 成员 | 作用 |
| --- | --- |
| `RequestID` / `CommandID` | 当前请求与命令 id |
| `Invocation` | 宿主注入的可信调用来源；缺省 `Source = "unknown"` |
| `CancellationToken` | 命令取消时被取消 |
| `SendAsync(event)` | 推给调用方（仅 interact） |
| `OnEvent(handler)` | 收调用方事件（仅 interact） |
| `HandleRequests(handler, concurrency?)` | 会话内 request handler；return 就是那条 request 的结果 |
| `Closed` | 等到调用方 end / 断开 |
| `Dependencies().Require(alias)` | 绑定当前 command parent / Profile 的依赖客户端 |
| `UI()` / `Events` / `Platform()` / `System()` | 与 Runtime 同源 |
| `Config` / `Storage()` | Profile 配置快照 / 本机持久存储 |
| `CreateResourceAsync` / `CreateResourceFromAsync` / `CreateResourceWriterAsync` | 命令作用域资源创建 |

命令处理器签名：

```csharp
public delegate Task<object?> CommandHandler(CommandContext context, JsonElement input);
```

返回结果或抛 `BppException`（保留 `code` 回传宿主）；其他异常映射为 `INTERNAL`。

---

## 跨 Brick 调用

调用方 manifest 必须在 `dependencies` 中声明目标 Brick 和允许调用的命令。业务代码只使用 alias，精确来源与版本由 Host 握手绑定：

```csharp
runtime.OnCommand("ask", async (ctx, _) =>
{
    var openAi = ctx.Dependencies().Require("openai");
    var result = await openAi.InvokeAsync(
        "chat",
        new Dictionary<string, object?> { ["prompt"] = "hello" },
        new InvokeOptions { ProfileId = "work" });
    return result;
});
```

有状态交互用 `InteractAsync`（必须传 `OnEvent`），说完用 `EndAsync`；`CallAsync` 是 Interact + 半关闭的糖。

命令内长期占用：`await ctx.Dependencies().Require(alias).StartAsync()`，返回 `StartedToolHandle`（`InvokeAsync` / `InteractAsync` / `CallAsync` / `DisposeAsync` / `StopAsync`），跟这次 Call，return 自动放手。命令外 `StartAsync` 抛 `PARENT_INVOCATION_REQUIRED`。

---

## 资源

普通 `InvokeAsync` 结果保持 `ResourceRef`；读取先 `OpenResource`：

```csharp
var handle = runtime.OpenResource(reference);
await using var stream = handle;             // ResourceHandle : Stream，按 gRPC 块读
var bytes = await handle.BytesAsync();       // 上限 200 MiB；更大用流式读取
await handle.SaveToAsync("out.bin");
```

创建：

```csharp
var note = await runtime.CreateResourceAsync("hello", new ResourceCreateOptions { Name = "note.txt" });
var big = await runtime.CreateResourceFromAsync(sourceStream, new ResourceCreateOptions { Name = "large.bin" });

var writer = await runtime.CreateResourceWriterAsync();
await writer.WriteAsync(chunk);
var handle = await writer.FinishAsync();      // 幂等
```

单帧 1 MiB、gRPC 消息 12 MiB、整份读取 200 MiB、并发上传 8。`string` 默认 `text/plain; charset=utf-8`，`byte[]` 默认 `application/octet-stream`。

---

## 窗口

```csharp
var win = await runtime.UI.CreateBrowserWindowAsync("ui/pet.html", new WindowOptions
{
    ["width"] = 480,
    ["height"] = 320,
});

var title = await win.GetTitleAsync();
await win.SetBoundsAsync(new Bounds { X = 10, Y = 20 });
var closeResult = await win.CloseAsync();     // closed | prevented | pending | not-found
unsub();                                      // IDisposable
```

- `ctx.UI()` 创建 **Call 窗口**（binding=call，随这次调用消失）；`Runtime.UI` 创建 **Session 窗口**（binding=session）。
- 105 个反射方法按 `specs/window-protocol.schema.json` 生成并强类型包装；`win.CallAsync(method, args)` 可兜底调用宿主新方法。
- `win.WebContents()` 提供 `webContents.*`；命令外发送必须带 parent（否则 `PARENT_INVOCATION_REQUIRED`）。
- `win.On(event, handler)` 订阅 `closed / focus / blur / resize / ...`；`win.ExposeAsync(method, handler)` 处理子窗 request。

---

## 错误

```csharp
throw new BppException("INVALID_INPUT", "text is required");
```

常见 code：`INVALID_INPUT` / `PROTOCOL_ERROR` / `COMMAND_NOT_FOUND` / `PARENT_INVOCATION_REQUIRED` / `DEPENDENCY_NOT_DECLARED` / `CANCELLED` / `RESOURCE_UPLOAD_CLOSED` / `RESOURCE_MATERIALIZATION_TOO_LARGE` / `INTERNAL`。错误码字符串与 Node / Go / Python 完全一致；gRPC status details 携带 `brickly.runtime.v1.BrickError`。

---

## 协议与版本

- 协议：`brickly.runtime.v1`（`Protocol.ProtocolVersion`）
- SDK 版本：`0.11.0`（`Protocol.SdkVersion`），与 Node / Go / Python 基线一致
- 生成绑定：`buf.gen.yaml` 的 csharp 插件输出到 `src/Syllm.Brickly.Sdk/Grpc/Generated`，由 `npm run check:runtime-proto` 做漂移检查（禁止手改）

## 构建与测试

```bash
dotnet build
dotnet test
```

测试自带 `FakeHost`（进程内假 Host：Registry / Platform / Resource / Event / Connector / BrickStorage），不依赖真实宿主。

## 发布

```bash
cd Brickly
node scripts/publish-dotnet-sdk.mjs 0.11.0            # 导出独立仓库 + dotnet pack + tag
node scripts/publish-dotnet-sdk.mjs 0.11.0 --dry-run  # 只校验
node scripts/publish-dotnet-sdk.mjs 0.11.0 --push-nuget --api-key <key>  # 可选推 NuGet
```
