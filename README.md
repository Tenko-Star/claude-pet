# claude-pet

Claude Code 的桌面状态伙伴。Claude Code 的 hook 把会话活动上报给本机的后台服务，服务把这些事件归约成一个状态，再推送给桌面上的像素小人。之后计划接入一个蓝牙"红绿灯"硬件，它会消费同一个状态流。

全部是 Windows 上的原生 C#（.NET 10），不用 Docker。

## 工作方式

```
Claude Code ──hook──▶ plugin/scripts/forward-hook.sh ──HTTP POST──▶ StatusHub.Service ──SignalR──▶ DeskPet.App
                                                     127.0.0.1:47821/hooks/<事件>    /hubs/status
```

1. **采集**：`plugin/` 是一个 Claude Code 插件，注册了 `SessionStart`、`SessionEnd`、`UserPromptSubmit`、`PreToolUse`、`PostToolUse`、`PostToolUseFailure`、`Notification`、`Stop`、`StopFailure` 这 9 个 hook，把 hook 的 JSON 原样 POST 给服务。脚本最多耗时 1 秒、不输出、总是 exit 0，服务没开时事件直接丢弃，不会影响 Claude Code。
2. **归约**：服务把事件写入一个 Channel，由唯一的后台任务按 `session_id` 维护每个会话的状态，超时（默认 30 分钟）的会话会被清掉。多个会话同时活跃时按优先级取一个：`Waiting > Working > Thinking > Idle`。
3. **分发**：客户端连接 SignalR Hub `/hubs/status`，收到两类消息：
   - `Snapshot`：当前总状态（`Idle` / `Thinking` / `Working` / `Waiting`），新客户端连上立刻收到一份，之后只在变化时推送。
   - `Event`：一次性事件 `Done`（任务完成）、`Error`（出错）和 `ToolFailure`（工具调用失败），不进入快照。

事件到状态的映射：

| Hook 事件 | 会话状态 |
| --- | --- |
| `SessionStart` | Idle |
| `UserPromptSubmit`、`PostToolUse` | Thinking |
| `PreToolUse` | Working |
| `PostToolUseFailure` | Thinking，并广播 `ToolFailure` 事件 |
| `Notification` | Waiting |
| `Stop` | Idle，并广播 `Done` 事件 |
| `StopFailure` | Idle，并广播 `Error` 事件 |
| `SessionEnd` | 移除该会话 |

## 目录结构

```
assets/                    像素素材（只读输入，manifest.json 是动画参数的唯一来源）
installer/                 Inno Setup 安装包脚本
plugin/                    Claude Code 插件，注册 hook（说明见 plugin/README.md）
scripts/                   Windows 服务的安装 / 卸载脚本、安装包构建脚本
src/
  StatusHub.Contracts/     共享 DTO：状态枚举、快照、事件、Hub 路径与方法名
  StatusHub.Service/       后台服务：接收 hook、归约状态、SignalR 广播
  DeskPet.App/             WPF 桌宠：透明置顶窗口，渲染像素小人
tests/                     与 src/ 对应的测试项目
```

## 环境要求

- Windows 10/11
- .NET 10 SDK（安装脚本用它发布服务；运行服务需要 .NET 10 运行时和 ASP.NET Core 运行时，装了 SDK 就都有）
- Claude Code，并且运行环境里有 `sh` 和 `curl`（原生 Windows 用 Git Bash 即可；WSL 发行版一般自带）

## 安装包

推荐的安装方式。构建安装包需要 .NET 10 SDK 和 [Inno Setup 6](https://jrsoftware.org/isinfo.php)（`winget install JRSoftware.InnoSetup`），不需要管理员权限：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Version 0.1.0
```

产物是 `artifacts\installer\ClaudePet-Setup-0.1.0.exe`。两个程序都自带 .NET 运行时，目标机器不用另装。运行安装包需要管理员权限，它会：

1. 安装后台服务和桌宠，并注册 `ClaudePetStatusHub` 服务（开机自动启动，崩溃后 5 秒重启）；
2. 以当前用户身份注册 Claude Code 插件（本机没有 `claude` 命令时会提示手动注册的命令）；
3. 创建开始菜单快捷方式；勾选"登录 Windows 时自动启动桌宠"时写入开机自启。

固定位置：

| 内容 | 位置 |
| --- | --- |
| 后台服务 | `C:\Program Files\ClaudePet\StatusHub\` |
| 桌宠和内置角色 | `C:\Program Files\ClaudePet\DeskPet\`（角色在 `characters\` 下） |
| Claude Code 插件 | `C:\Program Files\ClaudePet\plugin\` |
| hook 日志 | `C:\ProgramData\ClaudePet\hooks\` |
| 自己添加的角色 | `%LOCALAPPDATA%\ClaudePet\characters\` |
| 窗口位置、缩放、选中的角色 | `%LOCALAPPDATA%\ClaudePet\DeskPet\window.json` |
| 开机自启 | 注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `ClaudePet` 值 |

- **升级**：直接运行新版本的安装包，它会先停掉服务和桌宠，再覆盖安装。
- **卸载**：在"设置 → 应用"或开始菜单里卸载。会删除服务、插件注册和开机自启；hook 日志和自己添加的角色会保留。
- **插件**：
  - 安装包会把插件市场 `deskpet-local` 换成安装目录里的那份。如果之前从仓库目录注册过，会被替换。
  - 插件只对 Windows 上的 Claude Code 生效；Claude Code 跑在 WSL 里时，仍需按下文"3. 安装 Claude Code 插件"手动注册。
- **向导语言**：安装向导界面是英文，因为当前 Inno Setup 没有自带简体中文语言包。

下面"使用"一节是开发时从源码手动安装的方式。

## 使用

### 1. 安装后台服务

用**管理员身份**打开 PowerShell，在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1
```

脚本会：

1. 如果服务已存在，先停止并删除（所以重复执行就是"更新"）；
2. `dotnet publish` 到 `C:\Program Files\ClaudePet\StatusHub`；
3. 注册名为 `ClaudePetStatusHub` 的服务，开机自动启动，崩溃后 5 秒自动重启；
4. 启动服务。

可选参数：

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `-InstallDir` | `C:\Program Files\ClaudePet\StatusHub` | 发布目录 |
| `-DataDirectory` | `C:\ProgramData\ClaudePet\hooks` | hook 日志目录 |
| `-Port` | `47821` | 监听端口（只绑定 127.0.0.1） |

例如：`powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -Port 48000`。改了端口的话，插件也要改成同一个端口（见下文）。

服务以 LocalSystem 身份运行，所以日志默认放在 `C:\ProgramData` 下，而不是你用户目录的 `%LOCALAPPDATA%`。

常用命令：

```powershell
Get-Service ClaudePetStatusHub          # 查看状态
Restart-Service ClaudePetStatusHub      # 重启（需管理员）
Get-Content "$env:ProgramData\ClaudePet\hooks\hook-events.jsonl" -Wait -Tail 20   # 实时看收到的 hook
```

服务的警告和错误写在 Windows 事件查看器的"Windows 日志 → 应用程序"里，来源为 `StatusHub.Service`。

改了代码后，重新执行一次安装脚本即可更新。

### 2. 卸载服务

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1              # 只删除服务注册
powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1 -RemoveFiles # 同时删除发布目录
```

hook 日志（`C:\ProgramData\ClaudePet\hooks`）不会被删除，需要的话手动删。

### 3. 安装 Claude Code 插件

在仓库根目录执行一次，之后所有 Claude Code 会话都会自动加载：

```sh
claude plugin marketplace add ./plugin
claude plugin install deskpet-hooks@deskpet-local
```

- 确认已加载：`claude plugin list`
- 只想在单次会话里试用：`claude --plugin-dir ./plugin`
- 卸载：`claude plugin uninstall deskpet-hooks@deskpet-local`，再 `claude plugin marketplace remove deskpet-local`
- 端口不是 47821 时：在 Claude Code 里打开 `/config`，把插件的 `port` 选项改成同一个值

**Claude Code 跑在 WSL 里时**：路径换成 `/mnt/e/Code/claude-pet/plugin` 这样的形式，并在 `%UserProfile%\.wslconfig` 的 `[wsl2]` 下设置 `networkingMode=mirrored`，然后 `wsl --shutdown` 重启 WSL。这样 WSL 里的 `127.0.0.1` 才能访问到 Windows 上的服务。

更多细节和排错见 [plugin/README.md](plugin/README.md)。

### 4. 验证

1. 在 Claude Code 所在的环境里执行：

   ```sh
   curl -i -X POST http://127.0.0.1:47821/hooks/test -d '{}'
   ```

   返回 `HTTP/1.1 204 No Content` 说明服务可达。

2. 在 Claude Code 里发一条消息，让它用一次工具，然后看 `hook-events.jsonl` 是否出现新行。

### 5. 运行桌宠

```sh
dotnet run --project src/DeskPet.App
```

桌宠启动后会自动连接服务的 SignalR 状态流；服务先启动还是后启动都可以，断线会自动重连，断开期间显示空闲。

| 状态来源 | 动画 |
| --- | --- |
| 快照 Idle / Thinking / Working / Waiting | idle / think / working / notice |
| `Done` 事件 | done，播完回到当前快照对应的状态 |
| `Error` 事件 | error，一直停留到下一次提交提示词 |
| `ToolFailure` 事件 | 在当前画面上闪一下汗珠，不切换状态 |
| 空闲超过 5 分钟 | sleep |

配置在 `src/DeskPet.App/appsettings.json` 的 `DeskPet` 节：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `Scale` | `3` | 默认放大倍数（右键菜单改过之后以保存的为准） |
| `SleepAfter` | `00:05:00` | 空闲多久进入睡觉 |
| `HubUrl` | `http://127.0.0.1:47821/hubs/status` | 服务的状态流地址，端口要和服务一致 |

右键菜单（托盘图标的菜单相同）：缩放、角色（切换角色 / 打开角色目录）、开机自启、退出。

### 6. 角色

每个角色是一个文件夹，文件夹名就是角色 id：

```
<id>/
  manifest.json      状态、图层和动画参数（格式见 assets/runtime/manifest.json）
  sprites/*.png      图层图片
  character.json     可选：{ "name": "显示名" }，没有时显示 id
```

桌宠从两个地方读取角色：

| 位置 | 说明 |
| --- | --- |
| 程序目录下的 `characters\` | 内置角色，编译时从 `assets/runtime` 复制成 `characters\pixel-girl` |
| `%LOCALAPPDATA%\ClaudePet\characters\` | 你自己加的角色；右键菜单"角色 → 打开角色目录"可直接打开 |

- 用户目录里的角色和内置角色同 id 时，用用户目录里的。
- 往用户目录放进新角色、修改或删除角色文件，不用重启：菜单会自动更新，当前显示的角色会自动重新加载。
- 角色加载失败时（比如文件还没复制完），继续显示原来的角色。
- 选中的角色会被记住，下次启动沿用。

## 开发

```sh
dotnet build
dotnet test
dotnet run --project src/StatusHub.Service    # 前台运行服务，日志直接输出到控制台
```

前台运行时用的是 `Development` 环境，hook 日志默认写到 `%LOCALAPPDATA%\ClaudePet\hooks`。如果已经装了 Windows 服务，前台运行前先停掉服务，否则端口会冲突：`Stop-Service ClaudePetStatusHub`。

配置在 `src/StatusHub.Service/appsettings.json`：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `HookIngest:Port` | `47821` | 监听端口 |
| `HookIngest:DataDirectory` | 空（`%LOCALAPPDATA%\ClaudePet\hooks`） | hook 日志目录 |
| `Status:SessionTimeout` | `00:30:00` | 会话多久没有事件就视为过期 |
| `Status:ExpiryScanInterval` | `00:00:30` | 检查过期会话的间隔 |
