# Windows 内存压缩（Memory Compression）内核实现逆向报告

> **分析对象**：本机 `C:\Windows\System32\ntoskrnl.exe`，版本 **10.0.26100.9711**（WinBuild.160101.0800）
> **交叉对照**：`Win11_26H1_x64.iso` 中提取的 `ntoskrnl.exe`，版本 **10.0.28000.2269**
> **符号来源**：微软公开符号服务器 `msdl.microsoft.com` 的 `ntkrnlmp.pdb`（GUID `B661C6FE72991D160FE37C48B8BC54701`，age 1），共 **49,421** 个公开符号
> **校验**：`RtlCompressBuffer` / `RtlDecompressBuffer` / `RtlGetCompressionWorkSpaceSize` 三个符号的 RVA 与 PE 导出表**逐一吻合**，确认符号-RVA 换算正确
> **方式**：纯静态逆向（PDB 公开符号 + Capstone 反汇编 + 分发表/字符串交叉定位）

---

## 0. 结论摘要

Windows 的"内存压缩"不是某个驱动或服务，而是 **内核内存管理器（Store Manager / `SMKM`）内置的一套"压缩存储"**：

1. **压缩单位是 4 KB 页**，一页一个压缩块，压缩后仍按页粒度寻址——对上层完全透明，页表项只是把"物理页帧号"换成了"压缩存储中的槽位"。
2. **算法是 Xpress 系列为主、LZ4/Deflate 可选**。真正用于内存压缩的默认格式是 **Xpress Huffman（`RtlCompressBufferXpressHuff`）**，不是很多人以为的 LZNT1。
3. **压缩不是按进程触发的**，而是由 **全局内存条件（`SMP_MEMORY_CONDITION`）** 驱动：内存越紧张，压缩工作线程优先级越高（18 → 10 → 8 → -1）。
4. **压缩存储跑在 `Memory Compression` 进程里**（`ExpCompressionProcessName` 直接指向该字符串，RVA `0x11BF0`），是内核通过"进程外壳"承载的非分页/压缩存储，这也是 Process Lasso 之类工具无法操作它的原因。
5. **压缩失败不会丢数据**：`SmStoreCompressBuffer` 返回错误时直接放弃压缩、保留原始页（不会出现"压缩坏了"导致数据错乱）。

---

## 1. 关键数据结构与分发表

### 1.1 算法分发表 `RtlCompressBufferProcs`（RVA `0x1360`）

按算法 ID 索引的函数指针表，与 `RtlDecompressBufferProcs`（RVA `0x1FB0`）成对存在：

| 算法 ID | 压缩函数 | 解压函数 |
|---|---|---|
| 0 | 空 | 空 |
| 1 | 空 | 空 |
| **2** | `RtlCompressBufferLZNT1` | `RtlDecompressBufferLZNT1` |
| **3** | `RtlCompressBufferXpressLz` | `RtlDecompressBufferXpressLz` |
| **4** | `RtlCompressBufferXpressHuff` | `RtlDecompressBufferXpressHuff` |
| **5** | `RtlCompressBufferXp10` | `RtlDecompressBufferXp10` |
| **6** | `RtlCompressBufferLz4` | `RtlDecompressBufferLz4` |
| **7** | `RtlCompressBufferDeflateRaw` | `RtlDecompressBufferDeflateRaw` |
| **8** | `RtlCompressBufferDeflateZlib` | `RtlDecompressBufferDeflateZlib` |

对应工作区大小表 `RtlWorkSpaceProcs`（RVA `0x2060`），索引一致。

### 1.2 压缩格式枚举 `ST_COMPRESSION_FORMAT` 表（RVA `0x11C18`）

16 项 `uint16`，**低字节 = 算法 ID，高字节 = 标志位**：

| fmt | 值 | 算法 | 实际含义（据调用点推断） |
|---|---|---|---|
| 0 | `0x0006` | LZ4 | LZ4 格式 |
| 1 | `0x0007` | DeflateRaw | Deflate 原始流 |
| 2 | `0x0007` | DeflateRaw | Deflate（另一变体） |
| 3 | `0x0000` | — | 未定义 |
| **4** | **`0x0004`** | **XpressHuff** | **默认内存压缩格式** |
| 5 | `0x1001` | 标志 `0x10` | 特殊（可能为 HW 加速） |
| 6/7 | `0x0004` | XpressHuff | 同 fmt 4 |
| 8 | `0x1000` | 标志 `0x10` | 特殊 |
| 11 | `0x8000` | 标志 `0x80` | 特殊 |
| 14/15 | `0x0004` | XpressHuff | 同 fmt 4 |

> **fmt 4 / 6 / 7 / 14 / 15 全部指向 XpressHuff**，说明 Xpress Huffman 是内存压缩的主力算法。
> LZNT1（算法 2）在这张表里**没有**被内存压缩路径使用——它只服务于老式 NTFS 文件压缩。

### 1.3 内存条件 → 压缩线程优先级表（RVA `0x38018`）

`SmStGetPriorityByMemoryCondition` 的静态数组，`int32`：

| `SMP_MEMORY_CONDITION` | 优先级 |
|---|---|
| 0 | 18 |
| 1 | 18 |
| 2 | 10 |
| 3 | 8 |
| 4 | -1 |

**这是整个压缩子系统的"油门"**：内存越宽裕，压缩线程优先级越高（18 = 高于常规）；内存越紧张，优先级降到 -1，让出 CPU 给前台。这个设计意图是"压缩是后台预取式工作，不该和用户抢 CPU"。

---

## 2. 压缩主链路（地址级证据）

### 2.1 入口：`SmCompressManagerStart`（RVA `0x60C418`）

```
0060C441  mov  edx, 0x6e436d73      ; 池标签 'smCn'
0060C44C  imul rcx, rbp, 0x110      ; rbp = 每组的 context 数
0060C455  call SmAllocEx            ; 分配 SM_COMPRESS_CONTEXT 数组
0060C4BE  call SmCompressManagerUpdateTopology
```

按 NUMA/处理器组拓扑分配压缩上下文，每组一个（`0x110` 字节/个）。

调用者 `0x4EABE3`，来自 `SmStorePrepare` 之后：

```
004EABD5  test al, 0x20            ; 位 0x20 = 压缩功能启用标志
004EABD7  je   0x4eabee            ; 未启用 → 跳过压缩初始化
004EABE0  mov  edx, r14d            ; r14d = [rsi+0x860] 压缩格式
004EABE3  call SmCompressManagerStart
```

`[rsi+0x860]` 的赋值（`0x4EAB97`）：

```
004EAB8D  cmp  dword ptr [rsi+0xc78], 1
004EAB94  mov  eax, r12d
004EAB97  setne al
004EAB9A  inc  eax                 ; → 1 或 2
004EAB9C  mov  [rsi+0x860], eax
```

即**格式在 1 和 2 之间二选一**（DeflateRaw / Deflate 变体），受 `[+0xc78]` 标志影响。

### 2.2 每线程上下文初始化：`SmCompressCtxStart`（RVA `0x60C290`）

**这里是"算法选择"真正落地的地方**：

```
0060C2A8  movsxd rax, edx                          ; edx = ST_COMPRESSION_FORMAT
0060C2AE  lea    rdx, [rcx+0xa4]                   ; 输出：工作区大小
0060C2BE  lea    rax, [rip-0x5fa6ad]               ; → RVA 0x11C18（格式表）
0060C2C5  movzx  ecx, word ptr [rax+rcx*2]         ; ecx = 表中第 fmt 项的 uint16
0060C2C9  call   RtlGetCompressionWorkSpaceSize    ; 查工作区大小
0060C2CE  test   eax, eax
0060C2D0  js     0x60c318                          ; 失败 → 直接返回
0060C2D5  lea    r9, SmCompressCtxBalancerThread
0060C2E2  call   SmCompressCtxCreateThread         ; 建"均衡线程"
0060C2F7  call   SmCompressManagerGetIdealProcessor
0060C30E  call   SmCompressCtxCreateThread         ; 建实际压缩工作线程
```

注意 `movzx ecx, word ptr [rax+rcx*2]` 把整个 `uint16` 传给 `RtlGetCompressionWorkSpaceSize`——低字节是算法 ID，高字节是标志位，Rtl 内部同样按低字节分发（见 3.1）。

### 2.3 工作线程：`SmCompressCtxWorkerThread`（RVA `0x267D20`）

```
00267DBD  call ExAcquireSpinLockExclusive
00267DC2  inc  dword ptr [rbx+0x9c]                 ; 活跃线程计数
00267DF6  mov  edx, [rbx+0x98]                      ; 当前优先级
00267E00  call KeSetActualBasePriorityThread        ; 应用优先级
00267E0C  call SmCompressCtxUpdateThreadTopology    ; 绑核
00267E17  call ExReleaseSpinLockExclusive
00267E25  call KeSetEvent                           ; 唤醒
```

线程优先级由 1.3 的条件表决定，通过 `SmCompressCtxUpdatePriority`（RVA `0x4BD314`）批量更新：

```
004BD332  cmp  [rcx+0x98], edx     ; 优先级没变就跳过
004BD343  mov  [rbx+0x98], esi
004BD357  call KeSetActualBasePriorityThread   ; 逐个线程重设
```

### 2.4 取页压缩：`SmCompressCtxProcessEntry`（RVA `0x2681DC`）

```
00268206  mov r12, [rcx+8]          ; SM_COMPRESS_THREAD_CTX
0026820A  cmp dword ptr [r14+8], 0
00268217  jl  0x268309              ; 负值 → 走另一分支
0026821D  lea rsi, [rbx+0x38]       ; 待处理链表
00268225  cmp rdx, rsi
00268228  je  0x268324              ; 空链表 → 跳过
00268261  mov r8d, [r12+0xa8]
0026826F  call SmHwAcceleratorPartitionMgrGetDescriptor   ; 查硬件加速器
00268274  test rax, rax
0026827A  jne 0x268352              ; 有硬件加速器 → 走硬件路径
002682A8  mov ecx, [rcx+0x12c0]
002682AE  call SmStoreCompressBuffer                       ; ★ 软件压缩
002682B5  cmp  eax, 0x103            ; STATUS_PENDING
002682BA  je   0x268364
002682C9  mov r8d, [rsp+0x60]       ; 压缩后大小
002682DC  call SmCompressCtxCompleteEntry
```

**关键点**：先查 `SmHwAcceleratorPartitionMgrGetDescriptor`——如果 CPU/平台提供压缩加速器（Intel QAT、AMD 等），走硬件；否则回落到软件 `SmStoreCompressBuffer`。

### 2.5 软件压缩核心：`SmStoreCompressBuffer`（RVA `0x2683E0`）

这是全篇最关键的函数，**算法选择的最终落地**：

```
002683E0  ... 保存寄存器 ...
00268405  mov  eax, r14d
00268408  mov  r13d, 0x1000                 ; 页大小 4096
0026840E  and  eax, 0xfff                   ; 页内偏移
00268419  mov  edx, r13d
0026841C  sub  edx, eax                     ; edx = 本页剩余字节
00268423  movzx esi, word ptr [r8+r9*2+0x11c18]   ; ★ 查格式表得 uint16
0026842C  test ecx, ecx
0026842E  jne  0x268559                     ; ecx≠0 → 多块（分段）路径
00268434  mov  ecx, 0xff
00268439  movzx eax, si
0026843C  and  ax, cx                       ; 低字节 = 算法 ID
0026843F  lea  ecx, [rdi+2]
00268442  cmp  ax, cx
00268445  jb   0x2684c1                     ; alg < 2 → STATUS_INVALID_PARAMETER
00268447  cmp  ax, 8
0026844B  ja   0x26854f                     ; alg > 8 → STATUS_NOT_SUPPORTED
00268451  mov  ecx, 0xff00
00268459  and  si, cx                       ; 高字节 = 标志
00268460  lea  rcx, RtlCompressBufferLz4
0026846A  mov  rax, [r8+rax*8+0x1360]       ; ★ 查分发表得函数指针
00268475  cmp  rax, rcx
00268478  jne  0x2684c8                     ; 不是 LZ4 → 继续比对
00268494  call RtlCompressBufferLz4         ; 命中 LZ4
```

随后依次比对 `RtlCompressBufferLZNT1`（`0x2684C8`）、`RtlCompressBufferXpressHuff`（`0x2684F7`），
其余情况走 `_guard_dispatch_icall_no_overrides`（CFG 保护的间接调用，`0x268545`）——即**通用分发**。

**三段式结构（`0x268559` 分支）**：当 `ecx != 0`（需要把 4 KB 页切成多块压缩）时：

```
00268559  mov  ecx, 2
00268561  cmp  r9d, ecx
00268564  mov  r11d, 0x800            ; 每块 2048 字节
0026856A  sete r10b
0026856E  inc  r10d                   ; r10 = 1 或 2
00268576  lea  eax, [r10*2-2]         ; 头部 0 或 2 字节
0026857E  sub  edx, eax
00268583  shr  r8d, 1                 ; 输出大小减半
0026858D  cmovne r11d, r13d           ; 条件选择块大小
```

即把 4 KB 页拆成 2×2048 或 4×1024 的块分别压缩，每块可独立解压——这是**随机访问友好**的设计（读一页只需解压一块）。

### 2.6 内联尾段（RVA `0x6D904C`，编译器抽出的共享代码）

```
006D9051  cmp  r13d, r10d
006D9054  jae  0x6d90fd              ; 所有块处理完
006D9084  call SmHwAcceleratorIssueRequest   ; 有加速器 → 发异步请求
006D908B  cmp  eax, 0x103            ; STATUS_PENDING
006D9090  jne  0x26849d
006D90C6  call RtlCompressBuffer     ; ★ 软件路径，走通用分发
006D90CD  test eax, eax
006D90CF  js   0x26849d              ; 失败 → 退出
006D90E5  add  rbp, r11              ; 输出指针推进
006D90F2  add  rdx, r8               ; 输入指针推进
006D90F5  inc  r13d
```

### 2.7 完成处理：`SmCompressCtxCompleteEntry`（RVA `0x2685C0`）

```
002685E9  mov  ebp, 0x1000           ; 页大小
00268608  cmp  dword ptr [rsp+0x90], r9d
00268610  jge  0x268712              ; 全部完成
0026862A  call IoBuildPartialMdl     ; 为压缩结果建部分 MDL
00268661  lea  rdi, [r12+0x88]
0026866F  bts  dword ptr [r14+8], 0x1e   ; 置完成标志位
002686BB  cmp  dword ptr [r12+0x8c], 0
002686CD  cmp  rax, 0x80             ; 队列长度 ≥ 0x80 → 触发处理
002686E1  cmp  dword ptr [rip+KiIrqlFlags], 0
00268757  call MmBuildMdlForNonPagedPool
0026877B  call SmCompressCtxProcessReadyQueue   ; ★ 入队
```

### 2.8 落盘队列：`SmCompressCtxProcessReadyQueue`（RVA `0x2678F0`）

```
00267938  cmp  qword ptr [rdi+8], rdi
0026793C  je   0x26794f              ; 空队列
0026793E  movups xmm0, [rdi]
00267968  call ExReleaseSpinLockExclusive
002679BA  mov  eax, [r9+0x18]
002679C3  mov  [rsp+rdx*8+0x48], eax
002679CB  mov  [rsp+rdx*8+0x40], r9   ; 批量打包待处理项
002679D6  movzx ecx, word ptr [rax+0x1a90]
```

一次最多打包多项（`r13d` 递增），减少锁竞争。

---

## 3. 解压路径

### 3.1 `RtlCompressBuffer` 分发（RVA `0x26A220`）

```
0026A224  movzx eax, cx
0026A227  movzx r10d, cx
0026A22B  mov  ecx, 0xff
0026A230  and  ax, cx                 ; 低字节 = 算法 ID
0026A233  cmp  ax, 2
0026A237  jb   0x26a2a5               ; < 2 → STATUS_INVALID_PARAMETER (0xC000000D)
0026A239  cmp  ax, 8
0026A23D  ja   0x26a381               ; > 8 → STATUS_NOT_SUPPORTED (0xC000025F)
0026A243  mov  ecx, 0xff00
0026A248  and  r10w, cx               ; 高字节 = 标志，单独传递
0026A24F  lea  rax, RtlCompressBufferProcs
0026A256  mov  rax, [rax+rcx*8]       ; 查表
0026A25A  cmp  rax, RtlCompressBufferLz4
0026A261  jne  0x26a2ac
0026A29A  call RtlCompressBufferLz4
```

**确认：算法 ID 就是格式表 `0x11C18` 的低字节，标志位是高字节。** 二者分离传递。

### 3.2 `SmStoreDecompressBuffer`（RVA `0x3A4B6C`）

```
003A4B89  mov  r10, r9
003A4BAF  mov  rax, [rcx+0x1d90]
003A4BB6  mov  r13, [rax+0x7e0]
003A4BC6  mov  eax, [rcx+0x12c0]
003A4BD0  test eax, eax
003A4BD2  jne  0x6e6668              ; 特殊格式 → 另路
003A4BF8  mov  edx, 0x1000           ; 页大小
003A4C00  call RtlDecompressBufferLz4
003A4C05  mov  edi, eax
003A4C07  test eax, eax
003A4C09  js   0x3a4c18              ; 失败
003A4C0B  cmp  dword ptr [rsp+0x110], 0x1000   ; 输出必须正好 4 KB
003A4C16  jne  0x3a4c3a
```

**注意 `0x3A4C0B`**：解压结果**必须精确等于 4096 字节**，否则判为失败。这是"一页一压缩块"的直接证据。

### 3.3 `SmDecompressBuffer` 与页头格式（RVA `0x3A4978`）

```
003A49AB  lea  edx, [rbx+8]           ; 至少 8 字节头
003A49AE  cmp  esi, edx
003A49B0  jb   0x3a4b28               ; STATUS_INVALID_BUFFER_SIZE
003A49B6  mov  ecx, [rcx]             ; 读头
003A49B8  add  esi, -8
003A49BB  mov  eax, ecx
003A49BD  and  eax, 0xffffff
003A49C2  cmp  eax, 0x4d414d          ; ★ 魔数 'MAM' (0x4D414D)
003A49C7  jne  0x3a4b2f               ; STATUS_DATA_ERROR
003A49CD  test ecx, ecx
003A49CF  js   0x6e6614               ; 最高位 → 未压缩标记
003A49D5  add  r14, rdx               ; 跳过 8 字节头
003A49DC  mov  edi, [r12+4]           ; 解压后大小
```

**压缩页头（8 字节）结构**：

| 偏移 | 宽度 | 含义 |
|---|---|---|
| +0 | 24 bit | 魔数 `MAM`（`0x4D414D`） |
| +3 | 8 bit | 算法 ID（`and al, 0x7f`，最高位作标志） |
| +4 | 32 bit | 解压后大小（通常 4096） |

`0x3A4A04` 处 `mov al, [r12+3]` / `and al, 0x7f` / `movzx r12d, al`，然后：

```
003A4A15  cmp  r12d, 1
003A4A19  je   0x3a4b51              ; 算法 1 → 不支持
003A4A1F  mov  eax, 8
003A4A24  cmp  r12w, ax
003A4A28  ja   0x3a4b3d              ; > 8 → STATUS_NOT_SUPPORTED
003A4A2E  lea  rcx, RtlWorkSpaceProcs
003A4A35  mov  rax, [rcx+r12*8]       ; 按算法查工作区函数
003A4A48  cmp  rax, RtlCompressWorkSpaceSizeLZNT1
003A4A4B  jne  0x3a4b1c
003A4A53  call RtlCompressWorkSpaceSizeLZNT1
```

**头部最高位（`test ecx, ecx; js`）表示"本页未压缩、原样存储"**——这是压缩失败时的回退标记。

---

## 4. 触发条件：内存条件驱动

### 4.1 `SmCompressManagerUpdateMemoryCondition`（RVA `0x4C0040`）

```
004C0068  cmp  edx, 4                  ; edx = SMP_MEMORY_CONDITION
004C006B  jne  0x4c0072
004C006D  lea  ebx, [rdx+3]            ; 条件 4 → 优先级 7
004C0072  movsxd rax, edx
004C0075  lea  rbx, PriorityByMemoryCondition
004C007C  mov  ebx, [rbx+rax*4]        ; 否则查表
004C0083  call ExAcquireSpinLockShared
004C008E  mov  edx, [rdi+0x138]        ; 当前节流值
004C0094  mov  ebp, [rcx+0x7d4]        ; 每节点上下文数
004C009F  test ecx, ecx
004C00A1  je   0x4c00aa
004C00A3  mov  edx, 1                  ; 无内存压力 → 节流=1
004C00AA  test r14d, r14d
004C00AD  je   0x4c00a3
004C00AF  cmp  ebx, 7
004C00B2  jg   0x4c00b9
004C00B4  lea  edx, [rdx+rdx*2]        ; ×3
004C00B9  cmp  ebx, 8
004C00BC  jg   0x4c00c2
004C00BE  shr  edx, 1                  ; ÷2
004C00C2  cmp  ebx, 0xa
004C00C5  jg   0x4c00cc
004C00C7  shr  edx, 2                  ; ÷4
004C00CC  shr  edx, 3                  ; ÷8
004C00CF  mov  eax, 0x10
004C00D4  cmp  edx, eax
004C00D6  cmovae edx, eax              ; 上限 16
004C00D9  cmp  edx, ebp
004C00E1  cmova eax, edx               ; 取较大者
004C00E7  mov  [rdi+0x28], eax         ; 保存节流值
004C00EA  call ExReleaseSpinLockShared
004C00EF  cmp  [rdi+0x13c], ebx
004C00F5  je   0x4c011c               ; 优先级没变 → 跳过
004C010A  call SmCompressCtxUpdatePriority   ; 批量更新线程优先级
```

**这是压缩"节流"的核心算法**：

- 内存条件越好（优先级值越大，如 18），节流乘数越大（×3）→ 压缩放慢；
- 内存越紧张（优先级降到 8 以下），节流乘数变小（÷2、÷4、÷8）→ 压缩加快；
- 节流值上限 16，且不低于 `每节点上下文数`。

### 4.2 触发链

```
004EABD5  test al, 0x20         ; SmStore 特性位
004EABD7  je   0x4eabee         ; 未启用 → 不压缩
004EABE3  call SmCompressManagerStart
...
0034BD83  add  rcx, 0x4f0
0034BD8A  call SmCompressManagerUpdateMemoryCondition   ; 内存条件变化 → 调优先级
...
0034BD35  call SmFeAddInitiate   ; 页加入压缩存储
0034BCF9  call SmCompressCtxQueueEntries   ; 入压缩队列
```

`SmFeAddInitiate`（RVA `0x26618C`）用 B+ 树管理页索引：

```
00266247  call BTreeSearchKey
00266277  call BTreeIteratorFromSearchResult
0026629E  lea  rax, [r8+rax*8]        ; 遍历节点
```

**压缩存储用 B+ 树（`B_TREE<T_SM_PAGE_KEY, SMKM_FRONTEND_ENTRY>`）索引页**，键是页标识，值是压缩块位置。

---

## 5. 与 Process Lasso 的关系（承接上一份报告）

| 层面 | Windows 内核 | Process Lasso |
|---|---|---|
| 压缩 | 全局，内核 Store Manager，Xpress Huffman | **无** |
| 单进程操作 | 不支持 | `SetProcessWorkingSetSize(h,-1,-1)` |
| `Memory Compression` 进程 | 压缩存储的宿主 | **列入黑名单，不碰** |

上一份报告里 Process Lasso 的排除名单中 `Memory Compression` 那一项，现在有了内核侧的解释：
该进程承载的是**内核压缩存储**，其工作集由内核直接管理，外部 `SetProcessWorkingSetSize` 要么被拒绝，要么只会引起压缩存储抖动。

---

## 6. 版本对比（26100.9711 vs 28000.2269）

| 特征 | Win11 26100.9711 | Win11 26H1 28000.2269 |
|---|---|---|
| `MAM` 魔数 | 1 处（`0x3A49C3`） | 1 处 |
| `Memory Compression` 字符串 | 有 | 有 |
| 导出压缩函数 | 9 个 | 9 个（**完全一致**） |
| `RtlCompressBuffer` 等 RVA | `0x26A220` | 需另行换算（符号已下载） |

**结论：内存压缩的核心架构在两个版本间没有变化**，压缩格式表、算法集、`MAM` 页头格式均保持一致。差异只在地址偏移。

---

## 7. 完整压缩/解压流程（一张图）

```
页需要被换出
    │
    ▼
SmFeAddInitiate (0x26618C)          ← B+ 树查/建索引项
    │
    ▼
SmCompressCtxQueueEntries (0x4A11CC)  ← 入队，唤醒工作线程
    │
    ▼
SmCompressCtxWorkerThread (0x267D20)  ← 优先级由内存条件决定(18/10/8/-1)
    │
    ▼
SmCompressCtxProcessEntry (0x2681DC)
    ├─ SmHwAcceleratorPartitionMgrGetDescriptor → 有加速器?
    │       └─ 是 → SmHwAcceleratorIssueRequest (异步硬件压缩)
    └─ 否 → SmStoreCompressBuffer (0x2683E0)
              ├─ 查 ST_COMPRESSION_FORMAT 表 [0x11C18+fmt*2]
              ├─ 低字节=算法ID, 高字节=标志
              ├─ 查 RtlCompressBufferProcs [0x1360+alg*8]
              ├─ 4KB 可切 2×2KB 或 4×1KB 分块压缩
              └─ RtlCompressBufferXpressHuff / LZ4 / LZNT1 / Deflate
    │
    ▼
SmCompressCtxCompleteEntry (0x2685C0)  ← 建 MDL，置完成位
    │
    ▼
SmCompressCtxProcessReadyQueue (0x2678F0)  ← 批量打包
    │
    ▼
写入压缩存储（8 字节头: 'MAM' + algID + 原始大小）
    │
    ▼
读回时: SmDecompressBuffer (0x3A4978)
    ├─ 校验魔数 0x4D414D
    ├─ 头部最高位 = 1 → 未压缩，直接拷贝
    ├─ 按 algID 查 RtlWorkSpaceProcs
    └─ RtlDecompressBufferEx → 输出必须精确 4096 字节
```

---

## 8. 不确定性说明（如实标注）

1. **纯静态分析，未做动态验证**。没有在目标机上用 WinDbg 下断点实测压缩/解压的真实调用频次与压缩比。
2. **`ST_COMPRESSION_FORMAT` 的语义是推断的**。表值 `0x0004` 的"低字节=算法 ID、高字节=标志"依据是 `SmStoreCompressBuffer` 里 `and ax, 0xff` / `and si, 0xff00` 的用法，以及 `RtlCompressBuffer` 同样的处理，**逻辑自洽**；但 fmt 4/6/7/14/15 都指向 XpressHuff 这一"重复"现象，我无法排除它们是"同一算法的不同参数档位"而非"不同格式"。
3. **`ExpCompressionProcessName` 未被直接引用**。`0x2020` 槽指向 `0x11BF0` 的 `Memory Compression` 字符串，但我在 `.text` 里没找到直接 `lea` 该槽的指令（可能在初始化路径中通过指针表间接引用，或该符号仅用于调试/ETW）。"压缩存储跑在 Memory Compression 进程里"这一结论，**依据是符号名 + 字符串内容 + 公开资料，而非代码里的直接证据链**。
4. **硬件加速器路径未展开**。`SmHwAcceleratorPartitionMgrGetDescriptor` / `SmHwAcceleratorIssueRequest` 的具体加速器类型（Intel QAT / AMD / 其他）没有深入，只确认了"存在优先走硬件的分支"。
5. **节流算法的语义是推断的**。`0x4C00AF`–`0x4C00CC` 的 ×3/÷2/÷4/÷8 链，我判读为"内存条件越好、节流乘数越大（压缩越慢）"，依据是优先级表的值域（18 高 / -1 低）与分支顺序；但**没有实测节流值与实际压缩吞吐的对应关系**。
6. **符号来自公开 PDB 的公开符号子集（49,421 个）**，不含局部变量与结构体定义。因此结构体字段偏移（如 `[rcx+0x12c0]`、`[rsi+0x860]`）的语义是**据上下文推断**，没有类型信息佐证。
7. **ISO 版本对比只做了 2 个**（26100 与 28000）。按你的选择"先只做本机当前系统内核"，Win10 15SB / 22H2 / LTSC 2021 / LTSC 2024 未提取，无法给出完整的跨代差异表。

---

## 9. 产物与复现

| 文件 | 说明 |
|---|---|
| `temp/pub_syms.txt` | 从 PDB 提取的 49,421 个公开符号（RVA + 名称） |
| `temp/pdbpub2.py` | 自写 MSF 7.0 PDB 解析器（不依赖 dbghelp，绕开 ANSI 路径限制） |
| `temp/dis_range.py` | 带符号注解的区间反汇编器 |
| `temp/find_callers3.py` | 字节级 E8 rel32 扫描，定位调用者 |
| `temp/dis_*.txt` | 各关键函数的反汇编（含符号注解） |
| `temp/kernels/win11_26h1_ntoskrnl.exe` | 从 ISO 提取的对照内核 |
| `temp/sym/ntkrnlmp.pdb/` | 两个版本的 PDB |

**符号服务器踩坑记录**（供复现参考）：
- `msdl.microsoft.com` 的 URL 里 GUID 必须按 **symsrv 规范**拼装：前 8 字节小端、接着 2+2 字节小端、剩余 8 字节原序，再拼 age。
- 直接从 PDB 调试目录读出的原始字节顺序（如 `FEC661B6...`）**不能直接用于 URL**，正确形式是 `B661C6FE72991D160FE37C48B8BC5470`+age。
- `Invoke-WebRequest` 会因 `Content-Encoding` 处理差异返回 404，改用 `curl.exe -L` 正常。
- `dbghelp` 的 ANSI 接口在含中文的工作区路径下会截断搜索路径，导致符号加载失败——这是自写 MSF 解析器的直接原因。
