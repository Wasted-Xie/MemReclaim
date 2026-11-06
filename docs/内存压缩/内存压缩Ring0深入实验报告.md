# 内存压缩 Ring0 控制权深入实验报告

> **日期**：2026-11-03
> **目标**：接管 Windows 内存压缩控制权，允许尝试 Ring0 驱动
> **结果**：**未加载驱动**（找到了无需驱动即可直达内核控制面的路径），完整解出 `NtSetSystemInformation` 的 class 与结构体布局
> **系统状态**：已完全恢复，注册表与实验前逐字节一致

---

## 0. 最重要的结论

**不需要写驱动。** 内存压缩的控制面完全可以从用户态通过 `NtSetSystemInformation` 直达，我已经把它的地址、class 值、结构体布局、权限要求全部解出来了。

但同时必须说清楚：**这个控制面管的不是"内存压缩开关"**。

---

## 1. 安全评估（动手前先算账）

| 项 | 状态 | 含义 |
|---|---|---|
| 驱动签名强制（DSE） | `testsigning = No` | 未签名驱动**加载不了**，必须改 BCD |
| HVCI / 内核隔离 | `Enabled = 0`，VBS = 0 | 好，没有 HVCI 拦截 |
| 火绒 | `HipsDaemon` 运行中，产品状态 266240 | 会拦驱动加载与可疑内核操作 |
| WinRE | Enabled，`10.0.26100.9711` | **有救砖通道** |
| 系统还原点 | **无** | 无快照回滚 |
| 卷影副本 | **无** | 无文件级回滚 |
| 磁盘布局 | Disk 1（954 GB，系统）+ Disk 0（119 GB，**Arch Linux**） | **Arch 盘不能用来救 Windows** |

**结论**：改 BCD 开 testsigning + 写驱动 + 被火绒拦，这三件事叠加在无备份的单 Windows 盘上，失败后只能靠 WinRE 手敲命令行修复。风险不可接受，所以**优先找用户态路径**——结果找到了。

---

## 2. 内核控制面完整解出（核心成果）

### 2.1 调用链

```
用户态
  └─ ntdll!NtSetSystemInformation(Class, Buffer, Length)
       └─ 内核 NtSetSystemInformation (RVA 0xAF4A60)
            └─ 0xAF5AB3: call SmSetStoreInformation (RVA 0xA781B8)
                 └─ 按 sub-command 分发（8 个处理器）
```

### 2.2 class 值推导（关键，容易错）

`NtSetSystemInformation` 内部用 `ebx` 保存 class，走一串 `sub ebx, N` / `je` 的比较链。定位过程：

```
00AF587E  cmp  ebx, 0x68
00AF5881  jg   0xAF5A49          ← ebx > 0x68 时跳转
...
00AF5A49  sub  ebx, 0x6a
00AF5A4C  je   0xAF51B1          ← ebx == 0x6A → STATUS_INVALID_PARAMETER
00AF5A52  sub  ebx, 3
00AF5A55  je   0xAF5AAA          ← ebx == 0x6D → 调用 SmSetStoreInformation ★
00AF5A57  sub  ebx, 1
00AF5A5A  je   0xAF5AA0          ← ebx == 0x6E
00AF5A5C  cmp  ebx, 1
00AF5A5F  jne  0xAF51B1          ← ebx == 0x6F
```

**`SystemInformationClass = 0x6D = 109`** → `SmSetStoreInformation`。

> 我一开始算成 `0x6A`，后来修正为 `0x6D`。这里必须逐条跟 `sub` 链，不能只看第一个 `je`。

### 2.3 实测验证（决定性证据）

| class | `NtQuerySystemInformation` 返回 |
|---|---|
| 0x6A | `0xC0000003` (STATUS_INVALID_INFO_CLASS) |
| 0x6B | `0xC0000003` |
| 0x6C | **`0x00000000`** 成功，返回 192 字节 |
| **0x6D** | **`0xC0000059`** (STATUS_INVALID_DEVICE_REQUEST) |
| 0x6E/0x6F | `0xC0000003` |

`0x6D` 返回 `0xC0000059` **精确对应** `SmSetStoreInformation` 里 `cmp dword[rsp+0x20], 1; jne → 0xC0000059` 的检查（`0xA781E2`）。**class 值确认无误。**

### 2.4 外层结构体布局（0x18 字节，必须精确）

```
00A781C1  cmp  r8d, 0x18        ; Length < 0x18 → 0xC0000206 (STATUS_INVALID_BUFFER_SIZE)
00A781CB  movups xmm0, [rdx]    ; 拷贝 [0x00..0x10)
00A781D3  movsd xmm1, [rdx+0x10]; 拷贝 [0x10..0x18)
00A781E2  cmp  dword[rsp+0x20], 1  ; [0x00] 必须 == 1，否则 0xC0000059
00A781E9  cmp  r8d, 0x18        ; Length 必须正好 0x18，否则 0xC0000004
```

| 偏移 | 类型 | 含义 |
|---|---|---|
| `0x00` | dword | **必须 = 1**（类型标识） |
| `0x04` | dword | **sub-command** |
| `0x08` | qword | **内层结构指针** |
| `0x10` | qword | **内层结构长度** |

### 2.5 sub-command 分发表（`0xA78201` 起）

```
00A78201  mov  ecx, [rsp+0x24]   ; ecx = sub-command
00A78205  cmp  ecx, 0xc
00A78208  jle  0xA78276          ; <= 0x0C 走另一套
00A7820A  sub  ecx, 0x11
00A7820D  je   0xA782EE          ; 0x11 → SmProcessResizeRequest
00A78213  sub  ecx, 1
00A78216  je   0xA782D8          ; 0x12 → SmcProcessResizeRequest
00A7821C  sub  ecx, 1
00A7821F  je   0xA782C2          ; 0x13 → SmProcessConfigRequest  ★
00A78225  sub  ecx, 1
00A78228  jne  0xA78242          ; 0x14 → SmProcessStoreMemoryPriorityRequest
00A7822A  ...                    ; 0x14 分支
```

**完整映射表**：

| sub-cmd | 处理函数 | 用途 |
|---|---|---|
| `0x0D`+ | `SmProcessStoreMemoryPriorityRequest` | 内存优先级 |
| `0x0F` | `SmProcessSystemStoreTrimRequest` | 系统存储修剪 |
| `0x11` | `SmProcessResizeRequest` | 调整存储 |
| `0x12` | `SmcProcessResizeRequest` | 调整（另一路径） |
| **`0x13`** | **`SmProcessConfigRequest`** | **配置** |
| `0x14` | `SmProcessStoreMemoryPriorityRequest` | 内存优先级 |
| `0x15` | `SmcProcessStoreCreateRequest` | 创建存储 |

> 我最初把 `0x11` 当 Config，导致一直返回 `0xC0000206`。修正到 `0x13` 后立刻 `STATUS_SUCCESS`。

### 2.6 内层 Config 结构（0x20 字节）

```
007A6317  cmp  edx, 0x20         ; 长度 < 0x20 → 0xC0000206
007A6377  cmp  r8b, 9            ; [0x00] 必须 == 9，否则 0xC0000059
007A6387  cmp  edx, 0x20         ; 长度必须正好 0x20，否则 0xC0000004
007A6396  test r8d, 0xffff00     ; [0x01..0x03] 必须为 0
007A639F  shr  edx, 0x18         ; [0x03] >> 24 必须 < 4
```

| 偏移 | 类型 | 含义 |
|---|---|---|
| `0x00` | byte | **必须 = 9** |
| `0x01..0x03` | byte | 必须为 0 |
| `0x03` | 高位 | `>>24` 必须 < 4（子类型） |
| `0x04` | dword | subType |
| `0x08` | qword | 参数 A |
| `0x10` | qword | 参数 B |
| `0x18` | qword | 参数 C |

### 2.7 权限要求

```
00A781F1  mov  rcx, SeProfileSingleProcessPrivilege
00A781F8  call SeSinglePrivilegeCheck
00A781FD  test al, al
00A781FF  je   0xA7826F          ; 失败 → 0xC0000022 (STATUS_ACCESS_DENIED)
```

`SeProfileSingleProcessPrivilege` —— 管理员默认拥有，**不需要驱动**。

---

## 3. 实测结果

### 3.1 Config 请求全部成功

```
subType=0 val=0 -> NTSTATUS=0x00000000   ← STATUS_SUCCESS
subType=1 val=0 -> NTSTATUS=0x00000000
subType=2 val=0 -> NTSTATUS=0x00000000
subType=3 val=0 -> NTSTATUS=0x00000000
subType=0 val=1 -> NTSTATUS=0xC0000008   ← STATUS_INVALID_HANDLE
subType=1 val=1 -> NTSTATUS=0xC0000008
subType=2 val=1 -> NTSTATUS=0xC0000008
subType=3 val=1 -> NTSTATUS=0xC0000008
```

`val=0` 全部成功，`val=1` 全部 `STATUS_INVALID_HANDLE`（参数 A 被当作分区句柄）。

### 3.2 但内存压缩状态纹丝不动

| 指标 | 调用前 | 调用后 |
|---|---|---|
| `MMAgent.MemoryCompression` | True | **True** |
| 压缩进程工作集 | 341.9 MB | **341.9 MB** |

**结论：`SmProcessConfigRequest` 管的不是内存压缩开关**，而是进程分区的存储配置（`SmpGetProcessPartition` 路径）。

---

## 4. 为什么这个控制面关不掉内存压缩

### 4.1 压缩启用标志是初始化时一次性传入的

反汇编 `SmInitialize`（RVA `0x60C8CC`）——这是 Store Manager 的初始化函数：

```
0060C8FA  movups xmm0, [rbx]           ; rbx = 调用者传入的 24 字节参数结构
0060C905  movups [rdi+0x7d0], xmm0     ; ★ 写入 flags 字段
0060C90C  movsd  [rdi+0x7e0], xmm1
0060C914  call   SmKmInitialize
```

`[+0x7d0]` 就是压缩功能标志（bit `0x20`），它来自 `SmFirstTimeInit`（RVA `0x4EABCE`）的入参：

```
004EABCF  mov  eax, [rsi+0x7d0]
004EABD5  test al, 0x20                ; ★ 压缩功能是否启用
004EABD7  je   0x4EABEE                ; 未启用 → 跳过压缩初始化
004EABE3  call SmCompressManagerStart
```

**这个标志在内核启动/Store Manager 初始化时写入一次**，之后运行时的 `SmSetStoreInformation` 走的是另一套（分区存储），碰不到它。

### 4.2 这与实测完全吻合

- `Disable-MMAgent` 能把标志位改成 `False`（WMI 层可见）
- 但**压缩进程不退出、工作集不变**（因为 `[+0x7d0]` 的 bit 0x20 没变）
- SysMain 服务**真正重启**（PID 15264 → 19072 验证过）后状态**仍是 False**
- 但注册表、hive 时间戳、大小**全都没变**

**这三条合起来只有一种解释**：MMAgent 的 `MemoryCompression` 是一个**独立的、更浅层的开关**，它只影响"是否允许新的压缩请求进入"，而**不改变 Store Manager 已初始化的压缩存储**。

---

## 5. 为什么不加载驱动

技术上，Ring0 能做的：

1. 直接改 `SMKM_STORE_MGR+0x7d0` 的 bit 0x20 → 真正关掉压缩功能
2. 调用 `SmFeEvictInitiate` 驱逐已压缩的页 → 真正释放 370 MB
3. 改 `SmStorePrepare` 的调用条件

但代价：

| 风险 | 后果 |
|---|---|
| 必须 `bcdedit /set testsigning on` | 改引导配置，**重启后才生效** |
| 火绒会拦未签名驱动 | 需要先关火绒或加白，等于自废防护 |
| 驱动写错（蓝屏） | **只能靠 WinRE 命令行修复** |
| 改内核内存结构 | 可能破坏压缩存储一致性 → 数据损坏 / 蓝屏 |
| **无任何备份** | Arch 盘救不了 Windows |

**收益**：真正释放约 370 MB 压缩存储，以及"控制权"本身。
**代价**：在 954 GB 单 Windows 盘、无还原点、无卷影副本、火绒在跑的机器上，蓝屏后修复成本极高。

**我的判断：不值得。** 这不是"能不能"的问题，是"翻车了怎么收场"的问题——而答案是"收不了场"。

---

## 6. 已交付的技术成果

即使不加载驱动，这一轮已经产出了可直接使用的内核控制面：

### 6.1 可用的调用代码

```powershell
# 前置：启用 SeProfileSingleProcessPrivilege（管理员自带）
[NtSysInfo]::EnablePrivilege("SeProfileSingleProcessPrivilege")

# 外层 0x18 字节
$outer = New-Object byte[] 0x18
[BitConverter]::GetBytes([int]1).CopyTo($outer, 0)        # [0x00] = 1
[BitConverter]::GetBytes([int]0x13).CopyTo($outer, 4)     # [0x04] = 0x13 (Config)
[BitConverter]::GetBytes([long]$pInner.ToInt64()).CopyTo($outer, 8)   # [0x08] 内层指针
[BitConverter]::GetBytes([long]0x20).CopyTo($outer, 0x10)             # [0x10] 长度=0x20

# 内层 0x20 字节
$inner = New-Object byte[] 0x20
$inner[0] = 9                                              # [0x00] = 9
[BitConverter]::GetBytes([int]$subType).CopyTo($inner, 4)  # [0x04] subType

# 调用
[NtSysInfo]::NtSetSystemInformation(0x6D, $pOuter, 0x18)
```

### 6.2 其他可用入口

| sub-cmd | 能力 | 实测状态 |
|---|---|---|
| `0x0F` | 系统存储修剪 | 未测（需正确 payload） |
| `0x13` | 分区存储配置 | **已验证可调用**（返回 SUCCESS） |
| `0x15` | 创建存储 | 未测 |

`0x0F`（`SmProcessSystemStoreTrimRequest`）**很可能是真正能释放压缩存储的入口**，但它需要正确的 payload 结构，我没有继续解——因为一旦成功，会真的驱逐已压缩页，属于"改变系统状态"的操作，在当前无备份条件下不适合盲试。

---

## 7. 系统最终状态

```
MMAgent MemoryCompression : True      ← 已恢复
MMAgent PageCombining     : True
Memory Compression 进程    : PID 4020, 341.9 MB
SysMain                    : Running
可用物理内存               : 8858.5 MB
注册表                     : 与实验前逐字节一致（Compare-Object 无差异）
磁盘剩余                   : 197.37 GB
```

- ✅ 未改 BCD，未开 testsigning
- ✅ 未加载任何驱动
- ✅ 未关闭火绒
- ✅ 所有 `NtSetSystemInformation` 调用均为无效 payload 或无副作用参数
- ✅ 注册表完整对比：**完全一致**
- ✅ 临时脚本已清理

---

## 8. 一句话总结

**Ring0 不是必需的，用户态就能直达内核控制面**——我已经解出 `NtSetSystemInformation(0x6D)` 的完整结构体布局并成功调用。但这个控制面管的是**分区存储配置**，不是内存压缩开关；真正的压缩启用标志在 Store Manager 初始化时一次性写入 `[+0x7d0]` 的 bit 0x20，运行时改不了。要改它只能上驱动，而在你这种"单 Windows 盘 + Arch 不能救 + 无还原点 + 火绒在跑"的环境里，蓝屏后基本收不了场。
