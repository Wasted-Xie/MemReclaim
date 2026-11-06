# Process Lasso 单进程「内存压缩」原理逆向报告

> 分析对象：`Process Lasso/` 目录下的官方发行版（文件时间戳 2026-09-02）
> 分析方式：纯静态逆向（PE 结构解析 + IAT/延迟导入解析 + 基于 `.pdata` 函数边界的全量反汇编 + rip 相对引用与字符串交叉定位）
> 分析脚本与中间产物：`temp/`（`disasm_pdata.py`、`annotate.py`、`find_calls.py`、`func_refs.py` 等）

---

## 0. 结论先行（最重要的判断）

**Process Lasso 并没有对单个进程做「内存压缩」。**

它对单个进程执行的是 **工作集修剪（Working Set Trim）**：调用
`SetProcessWorkingSetSize(hProcess, (SIZE_T)-1, (SIZE_T)-1)`，
让 Windows 虚拟内存管理器把该进程工作集里「尽可能多」的物理页移出常驻集。
页被移出后进入 **备用列表（standby list）**，未被修改的页直接留在备用列表里当缓存，
被修改过的页写回页面文件 / 映射文件。整个过程**不产生任何压缩后的数据块**，
也没有「压缩比」「解压」这类语义。

佐证：

| 证据 | 说明 |
|---|---|
| 全二进制内 `SetProcessWorkingSetSize` 只有 2 个调用点（Governor）+ 1 个（GUI） | 且三个调用点都是 `min=max=-1` 的写法 |
| 二进制内不存在任何压缩算法实现 | 仅 GUI 里出现的 `deflate`/`bad compressed size` 等字符串属于 zip/minizip 支持（日志打包、更新包），不是进程内存压缩 |
| 中文语言包里**完全没有**「压缩」二字 | 官方中文把该功能叫「智能内存整理 / 整理内存」，对应英文 SmartTrim / Trim Memory |
| 出现 `Memory Compression` 字样的唯一位置是**排除名单** | 即 Windows 10/11 自带的内存压缩进程（`Memory Compression` / `memory compression`），Process Lasso 明确不去碰它 |

也就是说，用户感知到的「内存压缩」在 Process Lasso 里其实是 **SmartTrim（智能内存整理）** 的单进程动作，
以及可选的 **清理待机列表 / 清理文件缓存**。真正的「内存压缩」是 Windows 自己做的（Memory Compression 进程），
Process Lasso 只是把它列进黑名单。

---

## 1. 分析对象与模块职责

| 模块 | 大小 | 角色 |
|---|---|---|
| `ProcessGovernor.exe` | 1,378,288 B | 核心引擎（服务/后台调度器），所有内存动作的实际执行者 |
| `ProcessLasso.exe` | 2,631,664 B | GUI，负责菜单命令、以及「Trim Memory / Trim Now」的手动触发 |
| `pl_rsrc_*.dll` | 各约 1.9–2.1 MB | 语言资源（字符串表、对话框），不含逻辑 |

两个 exe 均为 PE32+（x64），无壳、无 VMProtect，`.text` 可正常线性反汇编。

---

## 2. 单进程修剪的完整调用链（核心）

### 2.1 最内层：真正干活的两条指令序列

**A. SmartTrim 主函数内的单进程路径**（函数区间 `0x14002F220 – 0x14003058B`）

```
0x14002FDC2  cmp  byte ptr [rsp+0x78], 1        ; 该进程是否被判定为「需要修剪」
0x14002FDC7  jne  0x14002FFC7                    ; 否 → 跳过
0x14002FDCD  mov  r8d, [r13+0x544]               ; r8d = 目标进程 PID
0x14002FDD4  xor  edx, edx
0x14002FDD6  mov  ecx, 0x100                      ; PROCESS_SET_QUOTA
0x14002FDDB  call [kernel32!OpenProcess]
0x14002FDE4  mov  r14, -1
0x14002FDF0  mov  r8, r14                         ; dwMaximumWorkingSetSize = (SIZE_T)-1
0x14002FDF3  mov  rdx, r14                         ; dwMinimumWorkingSetSize = (SIZE_T)-1
0x14002FDF6  mov  rcx, rax                         ; hProcess
0x14002FDF9  call [kernel32!SetProcessWorkingSetSize]   ★ 唯一的实质动作
0x14002FDFF  mov  rcx, rbx
0x14002FE02  call [kernel32!CloseHandle]
```

**B. 独立的单进程修剪包装函数**（`0x140041F60 – 0x140041FBC`，92 字节，被 watchdog 路径 `0x14001D111` 调用）

```
0x140041F66  mov  r8d, [rcx+0x544]      ; 进程对象里的 PID
0x140041F6D  xor  edx, edx
0x140041F6F  mov  ecx, 0x100            ; PROCESS_SET_QUOTA
0x140041F74  call [kernel32!OpenProcess]
0x140041F88  mov  rdx, -1
0x140041F94  mov  r8, rdx
0x140041F9A  call [kernel32!SetProcessWorkingSetSize]
0x140041FA8  call [kernel32!CloseHandle]
0x140041FAE  movzx eax, bl              ; 返回是否成功
```

**C. GUI 手动「Trim Memory / Trim Now」**（`0x1400F6A10 – 0x1400F6A67`，命令 ID `0x9C47+8` 分支，调用点 `0x14003F281`）

```
0x1400F6A16  mov  r8d, edx             ; PID
0x1400F6A19  mov  ecx, 0x100           ; PROCESS_SET_QUOTA
0x1400F6A20  call [kernel32!OpenProcess]
0x1400F6A46  call [kernel32!SetProcessWorkingSetSize]   ; rdx=r8=-1
```

> 三处调用的参数完全一致：`(hProcess, -1, -1)`。
> 按 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-setprocessworkingsetsize)，
> `min = max = (SIZE_T)-1` 的语义是「从该进程工作集中移除尽可能多的页」，等价于 `EmptyWorkingSet()`。

### 2.2 「工作集有多大」是怎么量的

Governor 没有静态链接 psapi，而是在初始化时用 `GetProcAddress` 动态取表（函数 `0x140040CC0`）：

```
0x140040D6E  lea  rcx, "psapi.dll"                 → LoadLibraryW
0x140040D92  lea  rdx, "EnumProcessModules"        → GetProcAddress → [+0x170]
0x140040DB7  lea  rdx, "GetModuleFileNameExW"      → GetProcAddress → [+0x168]
0x140040DD2  lea  rdx, "GetModuleBaseNameW"        → GetProcAddress → [+0x160]
0x140040DED  lea  rdx, "GetProcessMemoryInfo"      → GetProcAddress → [+0x178]
0x140040E08  lea  rdx, "QueryWorkingSet"           → GetProcAddress → [+0x180]
0x140040E1C  lea  rcx, "kernel32"  → "IsWow64Process"
```

单进程工作集取值的实际代码（`0x14002FB44` 起）：

```
0x14002FB44  mov  edx, [r13+0x544]
0x14002FB4B  call 0x140076560                     ; PID → 进程句柄（RAII 包装）
0x14002FB65  xorps xmm0, xmm0                     ; 清零 0x50 字节缓冲
0x14002FB7C  mov  rax, [r13+0x178]                ; = QueryWorkingSet
0x14002FB83  mov  r8d, 0x50                       ; 缓冲长度 80 字节
0x14002FB89  lea  rdx, [rbp-0x40]
0x14002FB8D  call rax
0x14002FBA0  mov  r15, [rbp-0x30]                 ; PSAPI_WORKING_SET_INFORMATION.WorkingSetSize
0x14002FBA9  mov  ecx, [rax+0x320c]               ; 配置：MinimumProcessWSSInMb
0x14002FBAF  mov  rax, r15
0x14002FBB2  shr  rax, 0x14                       ; 字节 → MB（2^20）
0x14002FBB6  cmp  rax, rcx
0x14002FBB9  seta al                              ; 工作集 > 阈值 → 1
```

- `QueryWorkingSet` 的 `0x50 = sizeof(PSAPI_WORKING_SET_INFORMATION)`，`[buf+0x10]` 即 `WorkingSetSize`（页数×页大小，按字节）。
- 若 `QueryWorkingSet` 返回失败，会先置状态码 `1`（`0x14002FB93`），并用 `r15` 的初值参与比较。

### 2.3 决策条件（三道闸门）

**闸门 1：系统 RAM 负载**（`0x14002FA51` 起）

```
0x14002FA46  mov  dword ptr [rbp-0x80], 0x40     ; MEMORYSTATUSEX.dwLength = 64
0x14002FA51  call [kernel32!GlobalMemoryStatusEx]
0x14002FA57  mov  ebx, [rbp-0x7c]                ; dwMemoryLoad（百分比）
0x14002FA63  cmp  [rax+0x3208], ebx              ; 配置：SmartTrimAutoMinimumRAMLoad
0x14002FA69  ja   0x14003010A                   ; 阈值 > 当前负载 → 直接退出，不修剪
```

- 配置键 `MemoryManagement/SmartTrimAutoMinimumRAMLoad`，**默认 `0x41` = 65%**（`0x14004AA4C` 与 `0x14005BDCF` 处赋默认值 0x41）。
- 即：内存占用低于 65% 时完全不动作，这是官方反复强调的「保守」。

**闸门 2：前台进程豁免**

```
0x14002FA6F  call [user32!GetForegroundWindow]
0x14002FA82  call [user32!GetWindowThreadProcessId]   ; → [rsp+0x5c] = 前台 PID
...
0x14002FBC3  cmp  [r13+0x544], eax                    ; 当前进程 PID == 前台 PID ?
0x14002FBCA  cmove ecx, ebx                           ; 相等 → 判定值清零（不修剪）
0x14002FBCD  mov  [rsp+0x78], ecx                     ; 最终「是否修剪」标志
```

**闸门 3：单进程工作集阈值**（见 2.2）

- 配置键 `MemoryManagement/MinimumProcessWSSInMb`，**默认 `0xC4` = 196 MB**（`0x14004AA5D`、`0x14005BE50`）。
- 只有工作集超过 196 MB 的进程才进入候选。

### 2.4 排除名单（黑名单）

`0x14002FCA0` 起遍历一个 NULL 结尾的宽字符串指针表（表基址 `0x14010C3D0`，比较函数 `0x1400AE2B0` = `wcscmp` 语义）：

```
0x14002FCA0  lea  rbx, [rip+0xdc729]     ; → 0x14010C3D0
0x14002FCB0  mov  rsi, [rbx]
0x14002FCBD  call 0x1400AE2B0            ; 进程名 vs 黑名单项
0x14002FCD0  call 0x1400AE2B0            ; 完整路径 vs 黑名单项
0x14002FCE1  lea  rax, [rip+0xdc6f0]     ; → 0x14010C3D8（表尾）
```

**表内容（Governor 内置，GUI 里另有一份 10 项的同义表）**

| # | 进程名 |
|---|---|
| 0 | `Memory Compression` |
| 1 | `memory compression` |
| 2 | `mfpmp.exe` |
| 3 | `msmpeng.exe`（Microsoft Defender 扫描进程） |
| 4 | `nissrv.exe`（Defender 网络检查） |
| 5 | `services.exe` |
| 6 | `smss.exe` |
| 7 | `wininit.exe` |
| 8 | `wudfhost.exe` |
| 9 | `zam.exe` |

GUI 侧同一张表位于 `0x14022F758` / `0x14022F760`，内容一致（只是顺序不同）。

**关键点**：`Memory Compression`（Windows 10 1803 之后把压缩存储跑在这个进程里）
被**显式排除**。Bitsum 的更新日志也写过同样的理由：
「Exclude Memory Compression process from SmartTrim (no access to act anyway)」。

### 2.5 批量循环与节流

```
0x14002FCED  call [kernel32!EnterCriticalSection]     ; 保护进程列表
0x14002FD10  ... 遍历列表 ...
0x14002FDB0  call [kernel32!LeaveCriticalSection]
0x14002FE0E  mov  edx, 0xFA                            ; 250 ms
0x14002FE16  call [kernel32!WaitForSingleObject]       ; 每个进程之间节流
0x14002FE08  inc  dword ptr [rbp+0x1a00]               ; 已处理计数
```

收尾时会取资源串（`LoadStringW`，ID `0x3DF`）拼出日志「已整理 %u 个进程，共有 %u 个」
（`0x140030211`–`0x1400302FA`，英文资源里对应 `Trimmed %u of %u processes.`）。

### 2.6 定时周期

SmartTrim 调度循环（`0x14002ECEE` 起）：

```
0x14002ED04  cmp  byte ptr [rax+0x32f1], bpl     ; SmartTrimIsEnabled
0x14002ED0D  mov  ecx, [rax+0x32fc]               ; SmartTrimIntervalMins
0x14002ED17  imul r9d, ecx, 0xEA60                ; 分钟 → 毫秒
0x14002ED1E  cmp  r9d, 0xEA60                     ; 下限 60000 ms
0x14002ED3F  call [kernel32!WaitForMultipleObjects]   ; 超时 = 间隔
```

- 配置键 `MemoryManagement/SmartTrimIntervalMins`，**默认 `0x0F` = 15 分钟**（`0x140056985` 的 `mov r9d, 0xf`）。

---

## 3. 单进程之外的两种「顺带清理」（同一函数内）

SmartTrim 主函数在修剪工作集之外，还能做两件**系统级**的事，都通过动态解析的 `ntdll!NtSetSystemInformation`：

### 3.1 清空备用列表（Standby List）

```
0x14002F7B3  cmp  byte ptr [rax+0x32f3], r13b     ; SmartTrimClearStandbyList
0x14002F7C0  mov  dword ptr [rsp+0x5c], 4         ; MemoryPurgeStandbyList
0x14002F7C8  mov  rax, [r12+0x10]                 ; = NtSetSystemInformation
0x14002F7CD  mov  r8d, 4                           ; 缓冲区长度
0x14002F7D3  lea  rdx, [rsp+0x5c]
0x14002F7D8  mov  ecx, 0x50                        ; SystemMemoryListInformation
0x14002F7DD  call rax
0x14002F7DF  test eax, eax
0x14002F7E1  js   0x14002F9FC                     ; NTSTATUS < 0 → 放弃
```

`0x50 = SystemMemoryListInformation`、命令值 `4 = MemoryPurgeStandbyList`，
与 [Geoff Chappell 的系统信息类表](https://www.geoffchappell.com/studies/windows/km/ntoskrnl/api/ex/sysinfo/set.htm) 一致。
该调用需要 `SeProfileSingleProcessPrivilege`（Governor 字符串表里有这个特权名，`0x1400EA740`，使用点 `0x140021546`）。

前置条件（`0x14002F7E7` 起）：`LogDisable`（`[+0x31ee]`）为 0 **且** `Logging/LogSmartTrim`（`[+0x31f5]`）为 1，
并且 `ClearStandbyFreeRAMThresholdMB`（默认 `0x400` = 1024 MB）判断通过（见 `0x14002F989`-`0x14002F9A4`）。

### 3.2 清空文件系统缓存

```
0x14002F68B  mov  ecx, 0x15          ; SystemFileCacheInformation
0x14002F690  call rax                ; NtSetSystemInformation(0x15, buf, 0x40)
```

缓冲区 64 字节，先把最小/最大工作集字段置 `-1`（`0x14002F674`、`0x14002F678`）再调用，
等价于 `SetSystemFileCacheSize(-1, -1, 0)`。默认关闭（`SmartTrimClearFileCache` 默认 false），
且官方资源里明确警告「除非访问模式发生剧烈变化，否则不推荐」。

### 3.3 ntdll 入口的动态解析

```
0x14002F040 函数：
   LoadLibraryW("ntdll.dll")
   GetProcAddress("NtSetSystemInformation")  → [0x140111450+...]
   GetProcAddress("NtQuerySystemInformation")→ ...
```

---

## 4. 「内存优先级」是另一套东西（不是压缩）

字符串表里有 `PagePriority` / `DefaultMemoryPriorities` / `Memory priority adjusted to %d (was %d)`，
实现走的是 `SetProcessInformation(ProcessMemoryPriority = 4, MEMORY_PRIORITY_INFORMATION{...}, 0xC)`：

```
0x140034E09  cmp  qword ptr [rip+...], 0      ; SetProcessInformation 是否可用
0x140034E20  call [kernel32!OpenProcess]      ; 0x200 = PROCESS_SET_INFORMATION
0x140034E30  mov  dword ptr [rsp+0x20], 1     ; MemoryPriority = 1 (VeryLow)
0x140034E38  cmp  edi, 1
0x140034E3B  mov  dword ptr [rsp+0x24], 1     ; PagePriority = 1
0x140034E43  mov  r9d, 0xC                    ; 结构体大小
0x140034E51  mov  edx, 4                      ; ProcessMemoryPriority
0x140034E5D  call [SetProcessInformation]
```

（同类调用还有 `0x140012A80`、`0x140034F80`、`0x140034F2F`。）

这属于「降低进程内存/页面优先级」，影响的是**页面被回收的先后顺序**，
和「压缩」无关，但常被用户和「压缩内存」混为一谈。

---

## 5. 为什么「单进程压缩」在技术上不成立（补充说明）

Windows 的内存压缩是**全局**行为：

- 由内核 Store Manager 把「冷」页压缩进 `Memory Compression` 进程持有的压缩存储，
  触发条件是全局内存压力，**没有 per-process 的开关或 API**。
- 应用层能对单个进程做的只有：改工作集上下限（trim）、改内存/页面优先级、改 I/O 优先级、
  改 CPU 亲和性/优先级。Process Lasso 全部用到了，但**没有**、也**无法**单独压缩某个进程。
- 若强行对 `Memory Compression` 进程做 trim，只会让压缩存储抖动、且通常直接 `Access Denied`，
  这正是它被写进黑名单的原因。

---

## 6. 证据索引（VA → 语义）

| 地址 | 内容 |
|---|---|
| `0x14002F220` | SmartTrim 主函数入口（栈帧 0x1AB0） |
| `0x14002FA51` | `GlobalMemoryStatusEx`，读 `dwMemoryLoad` |
| `0x14002FA6F` | `GetForegroundWindow` → 前台进程豁免 |
| `0x14002FB44` | 单进程 `QueryWorkingSet` 取工作集 |
| `0x14002FBB6` | 工作集(MB) 与 `MinimumProcessWSSInMb` 比较 |
| `0x14002FCA0` | 排除名单遍历（表 `0x14010C3D0`） |
| `0x14002FDF9` | ★ `SetProcessWorkingSetSize(h, -1, -1)` |
| `0x14002F68B` | `NtSetSystemInformation(0x15, ...)` 清文件缓存 |
| `0x14002F7D8` | `NtSetSystemInformation(0x50, cmd=4)` 清备用列表 |
| `0x14002ED17` | `SmartTrimIntervalMins * 60000` 作为等待超时 |
| `0x140041F60` | 单进程 trim 包装函数（watchdog 路径） |
| `0x140040CC0` | psapi 动态 API 解析（`QueryWorkingSet`/`GetProcessMemoryInfo`） |
| `0x1400F6A10` | GUI「Trim Memory」单进程实现 |
| `0x140034DF0` | `SetProcessInformation(ProcessMemoryPriority)` |

配置键（`MemoryManagement` 段，默认值均取自代码里的立即数）：

| 键 | 默认 | 含义 |
|---|---|---|
| `SmartTrimIsEnabled` | false | 总开关（默认关闭） |
| `SmartTrimWorkingSetTrims` | false | 是否修剪工作集 |
| `SmartTrimClearStandbyList` | false | 是否清备用列表 |
| `SmartTrimClearFileCache` | false | 是否清文件缓存 |
| `SmartTrimIntervalMins` | 15 | 检查周期（分钟，1–60） |
| `SmartTrimAutoMinimumRAMLoad` | 65 | RAM 负载阈值（%） |
| `MinimumProcessWSSInMb` | 196 | 单进程工作集阈值（MB） |
| `ClearStandbyFreeRAMThresholdMB` | 1024 | 清备用列表的空闲阈值（MB） |
| `ClearStandbyOnlyInPerfMode` | true | 仅在性能模式清备用列表 |
| `SmartTrimLogNoAction` | false | 无动作时是否也记日志 |

---

## 7. 不确定性 / 未验证项（如实标注）

1. **纯静态分析，未做动态验证**。以上结论来自反汇编与字符串/配置键交叉定位，
   没有在 x64dbg 里下断点实测（未在你的机器上运行过该软件）。
2. `QueryWorkingSet` 缓冲区只有固定 80 字节。工作集条目极多时会返回
   `STATUS_INFO_LENGTH_MISMATCH` 并需要重试；代码在 `0x14002FB91` 处只置了状态码 `1`
   并沿用旧值比较，**未实现扩容重试**。这可能是「工作集很大但没被修剪」的一个边界原因，
   但此处是静态推断，未实测。
3. `0x14002F68B` 的 `NtSetSystemInformation(0x15, ...)` 我把缓冲区前 8 字节判读为
   `min = max = -1`（对应 `SetSystemFileCacheSize(-1,-1,0)` 的「清空」语义），
   依据是 `0x14002F674/0x14002F678` 两次写入 `-1`，但缓冲区其余 0x38 字节初值未逐一追踪。
4. 排除名单在 Governor 里我确认到 10 项（表起于 `0x140109208`）；
   `0x14010C3D0` 那个 2 项短表（`Memory Compression` / `memory compression`）
   在 `0x14003FE95` 处被另一条逻辑使用，用途未完全确认（疑似「已知不可操作进程」分类）。
5. 动作 ID 与事件名（`PL_ACTION_TRIMMED_WORKING_SETS` 等）是日志事件注册表
   （`0x140043160`–`0x140044B5E`），我只确认了字符串与结构体字段，未逐条对应触发点。

---

## 8. 关于 open-reverselab

仓库已克隆到 `temp/open-reverselab`（GPL-3.0，未修改、未使用其 MCP 服务）。
其 `kb/pe-reverse/` 下的方法论（PE 结构解析、动态 API 解析链还原、`GetProcAddress` 表重建、
x64dbg 断点脚本生成）与本报告采用的路数一致；
但仓库内**没有** Process Lasso 相关的现成分析或脚本，
本报告结论全部来自对本地二进制文件的独立分析，不依赖该仓库的现成结论。
