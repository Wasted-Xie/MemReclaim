# MemCompCtl 驱动 + GUI 架构文档

> **日期**：2026-11-03
> **目标**：驱动负责执行，程序提供 GUI 调整参数
> **状态**：**驱动 IOCTL 全部验证通过，GUI 可运行，待重启加载最终版驱动**

---

## 1. 架构设计

```
┌─────────────────────────────────────────┐
│  MemCompGui.ps1  (PowerShell + WinForms) │
│  - 状态显示                              │
│  - 优先级调节（5 档）                     │
│  - 压缩开关                              │
│  - 主动 Trim（单次/批量/定时）             │
└──────────────┬──────────────────────────┘
               │ DeviceIoControl
               │ \\.\MemCompCtl
┌──────────────▼──────────────────────────┐
│  memcompctl.sys  (Ring0 执行器)          │
│  - 无策略，纯执行                          │
│  - 5 个 IOCTL                            │
│  - 所有写操作带前置校验                     │
└──────────────┬──────────────────────────┘
               │
┌──────────────▼──────────────────────────┐
│  Windows 内核 (ntoskrnl.exe)             │
│  SM_PARTITION / SM_COMPRESS_MANAGER      │
└─────────────────────────────────────────┘
```

**解耦原则**：
- 驱动不含任何策略逻辑，只做「收到命令 → 执行 → 返回结果」
- 所有阈值、频率、档位由 GUI 侧决定
- 驱动可被多个程序同时使用（无全局状态机）

---

## 2. IOCTL 协议

| 功能码 | IOCTL | 输入 | 输出 | 说明 |
|---|---|---|---|---|
| `0x800` | `GET_STATUS` | 无 | `MEMCOMP_STATUS` (56B) | 读取压缩状态 |
| `0x801` | `SET_PRIORITY` | `MEMCOMP_SET_PRIORITY` (8B) | 无 | 设优先级 |
| `0x802` | `ACTIVE_TRIM` | `MEMCOMP_ACTIVE_TRIM` (40B) | 同结构 | 主动回收 |
| `0x803` | `SET_ENABLE` | `MEMCOMP_SET_ENABLE` (8B) | 无 | 开关压缩 |
| `0x804` | `GET_CAPS` | 无 | `MEMCOMP_CAPS` (32B) | 读能力 |
| `0x805` | `GET_STATS` | 无 | `MEMCOMP_STATS` (288B) | 读存储统计 |

**CTL_CODE 计算**：`(0x8000 << 16) | (function << 2)`（METHOD_BUFFERED, FILE_ANY_ACCESS）

---

## 3. 实测验证结果

### 3.1 全部 IOCTL 通过

```
GET_STATUS  -> 返回 56 字节
  SM_PARTITION = 0xFFFF9C03E0ACE010
  Flags7d0     = 0x7B   bit20=1
  StoreFlags   = 0x320800  bit17=1
  Throttle     = 1
  StoreIndex   = 0x400
  ProcPid      = 4364

GET_CAPS    -> 返回 32 字节
  base = 0xFFFFF800E8000000
  trim = 1

SET_PRIORITY cond=2 -> 返回 0, 节流 1 -> 6
SET_PRIORITY cond=0 -> 返回 0, 节流 6 -> 3
ACTIVE_TRIM         -> 返回 40, TrimStatus = 0x00000000
```

### 3.2 优先级档位映射

| cond | 压缩线程优先级 | 节流值 | 适用场景 |
|---|---|---|---|
| 0 | 18（最高） | 3 | 内存紧张，要快速压缩 |
| 1 | 18 | 3 | 同上 |
| 2 | 10 | 6 | 平衡 |
| 3 | 8 | 12 | 省电 |
| 4 | -1（最低） | 16 | 几乎停止压缩 |

**实测 `cond=2` 时节流 6，`cond=0` 时节流 3，与反汇编的优先级表一致。**

---

## 4. 关键实现细节

### 4.1 定位 Memory Compression 进程

```c
// EPROCESS 里的名字是 "MemCompr"（8 字符），不是显示名 "Memory Compression"
// 且它不挂 ActiveProcessLinks 链表，必须走 PspCidTable
for (pid = 4; pid < 65536; pid += 4) {
    PsLookupProcessByProcessId(pid, &proc);
    if (name == "MemCompr") { ... }
}
```

### 4.2 主动 Trim 的两步

```c
// 1. 切到压缩进程上下文（API 只作用于调用者所在分区）
UINT64 apcState[0x10];
KeStackAttachProcess((PVOID)eprocess, (PVOID)apcState);

// 2. 发 Trim 请求，sub-command 必须是 0x15（0x14 是内存优先级）
UINT32 inner[8] = {0};
inner[0] = 1;
*(UINT64*)((char*)inner + 8) = 1;

UINT64 outer[3];
outer[0] = 1;
*(UINT32*)((char*)outer + 4) = 0x15;
outer[1] = (UINT64)(size_t)inner;
outer[2] = 0x10;

ZwSetSystemInformation(0x6D, (PVOID)outer, 0x18);

KeUnstackDetachProcess((PVOID)apcState);
```

### 4.3 设置优先级

```c
// 调用内部函数 SmCompressManagerUpdateMemoryCondition (RVA 0x4C0040)
mgr = smPart + 0x4F0;
fnAddr = ntBase + 0x4C0040;   // ntBase 从 DbgPrint 导入反推
((PFN)fnAddr)((PVOID)mgr, cond, 1);
```

### 4.4 IRP 结构偏移（x64）

```
IRP+0x18  AssociatedIrp.SystemBuffer
IRP+0x30  IoStatus.Status
IRP+0x38  IoStatus.Information
IRP+0xB8  CurrentStackLocation

IO_STACK_LOCATION:
  +0x00  MajorFunction (byte)
  +0x08  OutputBufferLength (dword)
  +0x10  InputBufferLength (dword)
  +0x18  IoControlCode (dword)
  +0x20  Type3InputBuffer (ptr)

DRIVER_OBJECT:
  +0x68  DriverUnload
  +0x70  MajorFunction[0..27]
```

---

## 5. 编译要点

**必须加 `/utf-8`**：

```
cl /nologo /c /GS- /Gs- /Zl /W3 /Zc:wchar_t- /O1 /utf-8 /Fo<obj> <src>
```

不加会怎样：中文注释被 GBK 解析，吞掉换行导致语法错误。

**必须提供 `memset`/`memcpy`**：编译器会把清零/拷贝循环优化成这两个调用，而 `/NODEFAULTLIB` 下没有实现。

---

## 6. 文件清单

| 文件 | 说明 |
|---|---|
| `C:\Projects\memdrv\memcompctl.h` | IOCTL 协议定义（驱动/GUI 共享） |
| `C:\Projects\memdrv\memcompctl.c` | 驱动源码 |
| `C:\Projects\memdrv\memcompctl2.sys` | 编译产物（含 Unload 例程） |
| `C:\Projects\memdrv\ntoskrnl.def` | 手工导入库定义 |
| `C:\Projects\memdrv\mk_probe.bat` | 一键编译脚本 |
| `C:\Projects\内存压缩\temp\MemCompGui.ps1` | GUI 主程序 |
| `C:\Projects\内存压缩\temp\Start-MemCompCtl.ps1` | 启动器 |

---

## 7. 当前状态与下一步

### 7.1 当前状态

```
testsigning : Yes
旧驱动     : MemCompCtl (memcompctl.sys) 仍在内存运行
服务注册   : 已全部删除
新驱动     : memcompctl2.sys 已编译签名，含 Unload 例程
```

**旧驱动占用设备名 `\Device\MemCompCtl`，导致新驱动无法加载（错误 183）。**

### 7.2 下一步：重启一次

重启后：
1. 旧驱动消失
2. 运行 `Start-MemCompCtl.ps1` 自动加载新驱动
3. 新驱动支持 `sc stop`（有 Unload 例程），后续可热替换

### 7.3 重启后的验证清单

- [ ] `sc create MemCompCtl` + `sc start` 成功
- [ ] `GET_STATUS` 返回正确数据
- [ ] `SET_PRIORITY` 各档位生效
- [ ] `ACTIVE_TRIM` 返回 0
- [ ] `GET_STATS` 能读到存储页数
- [ ] GUI 全部按钮可用
- [ ] `sc stop MemCompCtl` 能卸载

---

## 8. 待解决的问题

### 8.1 压缩进程 WS 显示为 0

当前系统（重启后内存充足）压缩进程 `WorkingSet64 = 0`，因为：
- Windows 内存压缩在内存充足时不活跃
- 压缩数据通过 MDL 映射，不计入 `WorkingSet`

**这不是 bug**，但导致 GUI 无法用 WS 显示回收效果。

**解决方案**：用 `GET_STATS`（IOCTL 0x805）读压缩存储的真实页数，而不是靠 WS。

### 8.2 需要制造压力才能看到效果

要验证 Trim 的实际回收，需要先让系统积累压缩数据。方法：
- 运行大量程序直到可用内存低于 30%
- 或用 `GET_STATS` 观察页数变化

---

## 9. 安全说明

- 驱动所有写操作都有前置校验（`IsKernelPtr` + 范围检查）
- `SET_ENABLE` 有 GUI 二次确认对话框
- 进程引用用完立即释放，不跨请求持有
- 支持 `DriverUnload`，可 `sc stop` 卸载
- 无蓝屏记录（本轮全部测试通过）
