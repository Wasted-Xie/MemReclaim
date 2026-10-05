# 内存回收 (MemReclaim)

按指定策略自动回收 Windows 内存中**不需要写回硬盘**的部分：
待机列表、待机列表（无优先级）、系统文件缓存，以及定时合并内存列表。

---

## 一、实测结论

### 1.1 四项清理功能全部可用

在管理员权限下实测（普通会话即可，**无需**计划任务提权）：

| 清理项 | 实测结果 |
|---|---|
| 待机列表 Standby list | **可用**（曾一次回收 904 MB） |
| 待机列表（无优先级） | **可用** |
| 系统文件缓存 System file cache | **可用** |
| 合并内存列表 Combine | **可用**（曾一次合并 82885 页） |

两项所需特权均能正常启用：

```
SeProfileSingleProcessPrivilege → 已启用 (RtlAdjustPrivilege)
SeIncreaseQuotaPrivilege        → 已启用 (RtlAdjustPrivilege)
```

### 1.2 关键：程序文件不能带「低完整性标签」

实测确认，四项清理能否工作，**取决于程序文件的完整性标签**，而非账户权限：

| 文件状态 | 进程完整性级别 | 四项清理 |
|---|---|---|
| 无标签（普通位置） | `High` | **全部成功** |
| 带 Low 标签（沙箱 / 受管目录） | `Low` | **全部失败** |

检查方法（在程序目录执行）：

```powershell
icacls MemReclaim.exe | Select-String "Mandatory Label"
```

出现 `Mandatory Label\Low Mandatory Level` 即为受限，必须按下节处理。

### 1.3 处理办法：必须用【复制】，不能用【移动】

这是最容易踩的坑。NTFS 的规则决定了两种操作结果完全不同：

| 操作 | 机制 | 结果 |
|---|---|---|
| **复制** | 创建新文件，继承目标目录属性 | 标签消失 → **可用** |
| **移动** | 同卷移动＝重命名，ACL 与标签原样保留 | 标签跟着走 → **仍然失败** |

所以：**右键 →「复制」→ 粘贴到普通目录（如 `C:\MemReclaim\`），再以管理员身份运行。**
把文件「移动」或「拖拽」过去是无效的。

程序启动时会自行检测并在必要时给出上述提示，无需手动排查。

> 注：这与杀毒软件无关。若怀疑被安全软件拦截，请以安全软件自身的拦截日志为
> 依据，不要仅凭「特权启用失败」就归因于安全软件——本工具的早期版本犯过这个
> 错误。另外，用 `icacls /setintegritylevel` 强行改标签会被 HIPS 类软件拦截，
> 不要尝试。

---

## 二、使用方法

### 2.1 编译

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

使用 Windows 自带的 .NET Framework 编译器，**不需要安装 .NET SDK**。
产物在 `dist/`：

- `MemReclaim.exe` —— 托盘常驻程序（无控制台窗口），清单 `requireAdministrator`
- `MemReclaimConsole.exe` —— 控制台版，清单 `asInvoker`，用于 `--selftest` / `--once`

两个 exe 由同一份源码编译，差别只在编译期嵌入的清单。

### 2.2 首次使用：先自检

```powershell
dist\MemReclaimConsole.exe --selftest
```

会逐项报告本机 4 个清理项是否可用，并给出失败原因。

### 2.3 启动托盘程序

**双击 `MemReclaim.exe`**，程序会自动请求管理员权限。

程序采用两道提权保障：

1. **嵌入 `requireAdministrator` 清单** —— 由 Windows 在启动时弹 UAC 提权（标准做法）
2. **运行时检测 + 自我重启** —— 清单未生效时兜底

第 2 道并非多余：当系统**关闭 UAC**（`EnableLUA=0`）时，`requireAdministrator`
清单会被 Windows 忽略，进程直接以调用者令牌运行。若调用者恰是非管理员，
就只能靠运行时检测来补救。

UAC 提示出现时，点「是」即可。若希望以后不再提示，可勾选 UAC 对话框中的
「始终信任此发布者」——**这是 Windows 官方的持久化授权机制**，比自建提权通道安全。

托盘菜单：
- **立即清理** —— 立刻执行一次全部已启用项
- **启用自动清理** —— 开关自动策略
- **设置…** —— 修改策略与清理项
- 双击图标同样打开设置

设置窗口底部另有三个操作按钮：

| 按钮 | 作用 |
|---|---|
| **检查权限** | 只报告完整性级别与特权状态，**不执行任何清理** |
| **立即清理** | 执行全部已启用项（含按配置的合并） |
| **合并内存页** | **只做页面合并**，不影响其他项 |

「合并内存页」是独立的手动入口，用途与「立即清理」不同：

- 合并会遍历物理页列表寻找内容相同的页面，开销高于待机列表清理，
  不适合频繁触发，因此单独成键按需执行
- 它**不受「清理项目」中合并复选框的约束**——既然手动点了这个按钮，
  意图就是执行合并，与是否纳入自动策略无关
- 结果会显示在下方日志区，例如「已合并 13169 页，折算 51.4 MB」
- 若显示「没有找到可合并的重复页面」，属正常情况，不是故障

### 2.4 命令行

```powershell
MemReclaimConsole.exe --once       # 执行一次清理后退出（供计划任务调用）
MemReclaimConsole.exe --selftest   # 环境自检
MemReclaimConsole.exe --help       # 帮助
```

`--once` 会同时把结果写入数据目录下的 `MemReclaim.log`。

> 命令行模式**不会**弹 UAC 提权。这是刻意的：计划任务下弹窗会挂住任务，
> 且自我重启会丢失控制台输出。权限不足时，`--selftest` 会如实报告原因。
> 需要提权请在计划任务中勾选「使用最高权限运行」。

### 2.5 部署到其他机器（给别人用）

直接分发 `dist\` 下的两个 exe 即可，无需安装、无需 .NET SDK
（Win7 及以上自带 .NET Framework 4.x）。

**部署时注意三点：**

**① 目标用户必须在 Administrators 组内。**

这是硬性要求，无法绕过。内存清理需要 `SeProfileSingleProcessPrivilege` 与
`SeIncreaseQuotaPrivilege`，二者的默认分配为：

```
SeProfileSingleProcessPrivilege = Administrators
SeIncreaseQuotaPrivilege        = Administrators, LOCAL SERVICE, NETWORK SERVICE
```

普通用户（Users 组）**不在其中**。微软对前者有明确说明：
*"This right shouldn't be granted to individual users."*

若目标用户不是管理员，则**没有安全的实现方式**——把特权授予 Users 组、
或通过 SYSTEM 服务中转，都会把本机暴露在提权风险下，也是杀软的重点拦截对象。
本程序不做这两件事。

**② 用「复制」，不要用「移动」。**

若程序文件带有「低完整性标签」（来自沙箱或受管目录），进程会被强制以降权
状态运行，四项清理全部失败。同卷移动会让标签跟着文件走，复制则会重新继承
目标目录属性。详见 1.2 / 1.3。

程序启动时会自动检测这种情况并给出提示，无需用户自行排查。

**③ 首次运行会被 Windows 拦一次，之后不会（程序会自动处理）。**

从网络分发（网盘、聊天工具、邮件）得到的 exe 会带上**网络标记**
（Mark-of-the-Web，即 `Zone.Identifier` 备用数据流）。附件管理器据此在双击时弹出：

> 打开文件 - 安全警告
> 无法验证发布者。你确定要运行此软件吗？

**这与代码签名无关**，是 MOTW 造成的。实测同一份 exe：带 MOTW 会弹框，
去掉 MOTW 就不弹，二者二进制完全相同。

程序在启动时会**自动清除自身的 MOTW**，所以用户只需在首次运行时点一次「运行」，
此后双击不再被拦。判定结果显示在「检查权限」的日志里，也会出现在 `--selftest` 输出中。

> **为什么不去买代码签名证书？**
> 对这个问题基本无效。签名只会把对话框里的「未知发布者」换成你的名字、图标从红叉变橙叉，
> **弹框依然出现**。要让签名生效，必须把证书导入每台目标机器的
> 「受信任的发布者」存储——那还不如直接清除 MOTW。
> 签名真正能解决的是 **UAC 提示**（见下），那是另一个问题。

**分发方式建议**：用 7-Zip 打包，让接收方用 7-Zip 解压。
Windows 内置解压**会**传播 MOTW，7-Zip 默认不会。这样接收方连第一次弹框都没有。

若只发单个 exe，接收方也可以手动解除，两种方式等效：

- 右键 exe → 属性 → 勾选「解除锁定」
- `Unblock-File <路径>`

## 三、策略说明

### 3.1 两种触发模式

**阈值模式（默认，推荐）**：可用内存低于设定百分比时清理，另有冷却时间。
可选叠加「待机列表超过 N MB 时清理」。

**固定周期模式**：每 N 秒清理一次，不看内存状况。间隔可设为 `0`（尽可能快执行），
此时实际频率由冷却时间决定，设置界面会直接显示生效间隔。

> 为什么默认用阈值：待机列表本质是系统的**磁盘缓存**。清空它会让后续磁盘读取
> 重新从硬盘加载，短期内系统反而变慢。频繁无脑清空是负优化，因此默认走
> 阈值 + 冷却，只在内存确实紧张时才动手。

### 3.2 冷却时间

两次清理的最小间隔（默认 300 秒，可设为 `0`）。设置为 `0` 时冷却不生效，
清理频率完全由触发模式决定。

> 提示：周期模式中间隔设为 0、冷却设为 0，会让程序在每次采样（1 秒）后都执行
> 清理，即每秒清空一次待机列表。这会导致磁盘缓存持续失效、系统明显变慢，
> 通常不是想要的效果。此处不设下限是尊重你的选择，但请谨慎使用。

### 3.3 合并内存列表的触发方式

合并会遍历物理页列表寻找内容相同的页面去重，**开销高于其他所有清理项**，
因此它的触发条件独立于主清理，提供两种方式，**各有独立开关、可任意组合**：

| 方式 | 默认 | 说明 |
|---|---|---|
| **定时** | 开启，30 分钟 | 每 N 分钟执行一次；N 设 `0` 表示不定时 |
| **阈值** | 关闭 | 内存**占用**超过 X% 时执行 |

两种方式**任一满足即触发**，但都要先通过共用的**冷却时间**（默认 300 秒）。
冷却不可省略：阈值在内存持续高占用时每秒都会成立，没有冷却会导致合并被反复
调用。

两个开关**都关闭**时，合并不参与任何自动触发，仅保留「合并内存页」手动按钮。

> **关于「内存占用」这个指标**：界面上写的是占用率，与主清理的「可用率」互补——
> 占用 85% 等价于可用 15%。分开表述是为了贴合任务管理器的读数习惯。
>
> **关于实际收益**（实测数据，供设定阈值参考）：稳态下每次合并约 100 MB，
> 占 16 GB 内存的 0.65%。它的产出取决于系统当前有多少重复页（如多个进程加载
> 同一 DLL），**无法通过提高频率来增加**。因此默认以定时为主，
> 阈值方式留给内存确实吃紧时按需启用。

### 3.4 通知行为

**默认不发送任何通知。** 清理在后台静默完成，不弹气泡、不打扰使用。

唯一的例外：**清理失败时仍会提示**。

这是刻意的设计——如果失败也一并静默，会出现「程序看起来在工作、实际每次都失败」
的情况，用户无从察觉。成功静默、失败告知，既安静又不会漏掉问题。

若想连成功也看到提示，可在设置里勾选「清理成功后显示气泡通知」。

> 另：「权限不足」这类启动期异常始终会弹窗说明，不受此开关影响。
> 这类问题若不提示，用户只会看到清理无效却找不到原因。

### 3.5 关于「不会写回硬盘」的那些项

按你的说明，以下项在回收时不产生磁盘写入，本工具实现的就是它们：

| 项 | 是否实现 | 需要的特权 |
|---|---|---|
| Standby list | 是 | SeProfileSingleProcess |
| Standby list (without priority) | 是 | SeProfileSingleProcess |
| System file cache | 是 | SeIncreaseQuota |
| Combine memory lists | 是 | SeProfileSingleProcess |
| Working set（干净部分） | 未实现 | — |

Working set 未实现的原因：`MemoryEmptyWorkingSets` 会把**所有**进程的工作集
整体清空，包含脏页，无法只清「干净」部分，与你的要求不符，故未纳入。

---

## 四、实现依据

所有 API 用法均经源码级核实，非凭记忆编写：

| 项 | 信息类 | 结构体 | 证据 |
|---|---|---|---|
| 待机列表 | `SystemMemoryListInformation` 0x50 | `SYSTEM_MEMORY_LIST_COMMAND` = 4 | Mem Reduct `main.c` |
| 待机列表（无优先级） | 同上 | = 5 (`MemoryPurgeLowPriorityStandbyList`) | 同上 |
| 系统文件缓存 | **`SystemFileCacheInformationEx` 0x51** | `SYSTEM_FILECACHE_INFORMATION` | 同上 |
| 合并内存列表 | `SystemCombinePhysicalMemoryInformation` 0x82 | `MEMORY_COMBINE_INFORMATION_EX`（24 字节，全零） | 同上 |

两个易错点（已避开）：

1. **文件缓存用的是 0x51 而非 0x15**。Mem Reduct 的日志字符串写的是
   `SystemFileCacheInformation`，但真实调用的信息类是 `SystemFileCacheInformationEx`。
2. `SYSTEM_MEMORY_LIST_INFORMATION` 含两个 `SIZE_T[8]` 内联数组（x64 共 176 字节），
   用 `Marshal.PtrToStructure` 解析会错位。本工具改用固定偏移逐字段读取，
   并已用性能计数器交叉验证。

来源：
- [Geoff Chappell — SYSTEM_INFORMATION_CLASS](https://www.geoffchappell.com/studies/windows/km/ntoskrnl/inc/api/ntexapi/system_information_class.htm)
- [Geoff Chappell — ZwSetSystemInformation](https://www.geoffchappell.com/studies/windows/km/ntoskrnl/api/ex/sysinfo/set.htm)
- [Mem Reduct 源码](https://github.com/henrypp/memreduct/blob/master/src/main.c)

---

## 五、文件结构

```
内存回收/
├─ build.ps1                 编译脚本（产出两个 exe）
├─ src/
│  ├─ Native.cs              P/Invoke、结构体偏移、特权启用
│  ├─ MemoryState.cs         内存状态读取（固定偏移解析）
│  ├─ MemoryCleaner.cs       4 项清理实现
│  ├─ TriggerEngine.cs       触发引擎（阈值/周期 + 冷却）
│  ├─ Storage.cs             数据目录（%APPDATA%，含旧配置迁移）
│  ├─ Config.cs              配置与持久化
│  ├─ TrayApp.cs             托盘程序
│  ├─ SettingsForm.cs        设置界面
│  ├─ SelfTest.cs            自检
│  ├─ ConsoleOut.cs          安全控制台输出（winexe 无控制台时不崩溃）
│  ├─ MotwHelper.cs          网络标记（MOTW）检测与清除
│  ├─ AutoStart.cs           开机自启（HKCU\...\Run）
│  ├─ IntegrityCheck.cs      完整性级别检测与建议
│  ├─ ElevationHelper.cs     提权处理
│  └─ Program.cs             入口与命令行
├─ dist/                     编译产物
│  ├─ MemReclaim.exe         主程序（requireAdministrator）
│  └─ MemReclaimConsole.exe  控制台版（asInvoker）
└─ temp/                     诊断工具与实测记录
```

配置文件：`%APPDATA%\MemReclaim\MemReclaim.config.xml`（可直接编辑）
日志文件：同目录下的 `MemReclaim.log`、`MemReclaim.selftest.txt`

清单文件：`src/app.manifest`（主程序）、`src/app-console.manifest`（控制台版）。
两者差异仅在 `requestedExecutionLevel`。

---

## 六、风险提示

1. **清空待机列表会丢弃磁盘缓存**。清理后首次读取大文件会明显变慢。
   这是正常现象，不是故障。这也是默认采用阈值模式的原因。

2. **需要管理员权限**。缺权限时程序会在托盘提示，并在设置窗口显示警告；
   自检可查看具体哪一项失败。

3. **合并内存列表开销较高**。默认 30 分钟一次。若你观察到周期性卡顿，
   把周期调长或关闭该项。

4. **程序文件不能带「低完整性标签」**（详见 1.2 / 1.3）。必须用**复制**的方式
   把程序放到普通目录，用**移动**的方式无效。程序启动时会自动检测并提示。

5. **从网络分发得到的文件首次运行会被 Windows 拦一次**（MOTW，详见 2.5）。
   程序会自动清除自身标记，第二次起不再拦截。

6. **本程序不做内存压缩**。内存压缩（任务管理器里的「内存压缩」）不能由用户态
   指定目标内存触发，唯一手段是系统级清空工作集，且效果约 6 秒达峰、100 秒内消散，
   定时反复触发的净收益接近于零。详细实测见 `内存压缩触发机制研究.md`。

7. **本工具不会写回脏页**。所实现的操作只回收「干净」页面，
   不会触发磁盘写入，也不会丢失数据。
