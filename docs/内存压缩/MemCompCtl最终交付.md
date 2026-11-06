# MemCompCtl：驱动 + GUI 最终交付

> **日期**：2026-11-04
> **状态**：**已完成，驱动运行中，GUI 可用，两个功能全部验证通过**

---

## 1. 架构

```
┌─────────────────────────────────────┐
│  MemCompGui.ps1  (PowerShell WinForms) │
│  职责：策略、参数、界面               │
│  - 优先级档位选择（5 档）              │
│  - 主动压缩：单次 / 批量 / 定时         │
│  - 当前档位由 GUI 自己记录             │
└──────────────┬──────────────────────┘
               │ DeviceIoControl
               │ \\.\MemCompCtl
┌──────────────▼──────────────────────┐
│  memcompctl.sys  (Ring0 执行器)       │
│  职责：只执行，不含策略                │
│  - SET_PRIORITY  设优先级              │
│  - ACTIVE_TRIM   主动压缩              │
└──────────────┬──────────────────────┘
               │
┌──────────────▼──────────────────────┐
│  Windows 内核 (ntoskrnl.exe)          │
└─────────────────────────────────────┘
```

**解耦原则**：
- 驱动**只做两件事**，不提供状态查询、能力查询、统计读取、开关控制
- 所有阈值、频率、档位由 GUI 决定
- 驱动无全局状态机，可被多个程序调用

---

## 2. IOCTL 协议（只有两个）

| 功能码 | IOCTL | 输入 | 输出 | 说明 |
|---|---|---|---|---|
| `0x801` | `SET_PRIORITY` | 8 字节 | 无 | 设置压缩线程优先级 |
| `0x802` | `ACTIVE_TRIM` | 32 字节 | 32 字节 | 主动压缩回收 |

**CTL_CODE 计算**：`(0x8000 << 16) | (function << 2)`

### 2.1 SET_PRIORITY

```c
typedef struct {
    unsigned long Cond;   // 0/1=最高(18), 2=中(10), 3=低(8), 4=最低(-1)
    unsigned long Flag;   // 通常 1
} MEMCOMP_SET_PRIORITY;
```

内部调用 `SmCompressManagerUpdateMemoryCondition(mgr, cond, flag)`（RVA `0x4C0040`）。

### 2.2 ACTIVE_TRIM

```c
typedef struct {
    unsigned long      Flags;      // 低字节 1 或 2
    unsigned long      Reserved;
    unsigned long long Val8;       // 内层 [0x08]，必须非零
    long               Status;     // 返回内核 NTSTATUS
    unsigned long      Reserved2;
    unsigned long      Reserved3;
} MEMCOMP_ACTIVE_TRIM;
```

内部流程：
1. `KeStackAttachProcess(MemCompr)` 切换上下文
2. `ZwSetSystemInformation(0x6D, sub=0x15, ...)` 发 Trim 请求
3. `KeUnstackDetachProcess` 恢复

---

## 3. 实测结果

### 3.1 功能 1：设置优先级

```
cond=0 -> ret=0    (最高，节流 3)
cond=2 -> ret=0    (中，节流 6)
cond=3 -> ret=0    (低，节流 12)
cond=4 -> ret=0    (最低，节流 16)
cond=0 -> ret=0    (恢复最高)
```

节流值变化完全符合优先级表。

### 3.2 功能 2：主动压缩

```
第 1 次: Status=0x00000000
第 2 次: Status=0x00000000
第 3 次: Status=0x00000000
第 4 次: Status=0x00000000
第 5 次: Status=0x00000000
```

全部 `STATUS_SUCCESS`。

### 3.3 边界校验

```
cond=99    -> 0xC000000D (STATUS_INVALID_PARAM)  正确拒绝
Trim flags=2 -> Status=0                          正常
```

---

## 4. 关键技术点

### 4.1 定位 Memory Compression 进程

```c
// EPROCESS 里的名字是 "MemCompr"（8 字符），不是显示名
// 且不挂 ActiveProcessLinks 链表，必须走 PspCidTable
for (pid = 4; pid < 65536; pid += 4) {
    PsLookupProcessByProcessId(pid, &proc);
    if (name == "MemCompr") { ... }
}
```

### 4.2 关键偏移（全部反汇编验证）

```
EPROCESS+0x758        Partition 对象
  +0x18               SM_PARTITION
SM_PARTITION+0x4F0    SM_COMPRESS_MANAGER
  +0x28               节流值
```

### 4.3 ntoskrnl 基址反推

```c
ntBase = *(UINT64*)(&__imp_DbgPrint) - 0x358CB0;   // DbgPrint 的 RVA
```

### 4.4 DRIVER_OBJECT 偏移（实测验证）

```
+0x68  DriverUnload
+0x70  MajorFunction[0]
```

**注意**：上一轮我误改成 `0x70`/`0x78`，导致 `CreateFile` 触发卸载例程、设备被删、IOCTL 全失效。改回 `0x68`/`0x70` 后正常。

### 4.5 IRP 偏移（x64）

```
IRP+0x18  AssociatedIrp.SystemBuffer
IRP+0x30  IoStatus.Status
IRP+0x38  IoStatus.Information
IRP+0xB8  CurrentStackLocation
```

---

## 5. 编译要点

```
cl /nologo /c /GS- /Gs- /Zl /W3 /Zc:wchar_t- /O1 /utf-8 /Fo<obj> <src>
link /nologo /OUT:<sys> /DRIVER /SUBSYSTEM:NATIVE,10.00 /ENTRY:DriverEntry \
     /NODEFAULTLIB /MACHINE:X64 /FIXED:NO /DYNAMICBASE <obj> ntoskrnl.lib
```

**两个必需的坑**：

1. **`/utf-8` 必须加** —— 否则中文注释被 GBK 解析，吞掉换行导致大量语法错误
2. **必须自己实现 `memset` / `memcpy`** —— 编译器会把循环优化成库调用，而 `/NODEFAULTLIB` 下没有实现

---

## 6. 文件清单

| 文件 | 说明 |
|---|---|
| `C:\Projects\memdrv\memcompctl.h` | IOCTL 协议定义（驱动/GUI 共享） |
| `C:\Projects\memdrv\memcompctl.c` | 驱动源码（约 330 行） |
| `C:\Projects\memdrv\memcompctl_f.sys` | 编译产物（当前加载版本） |
| `C:\Projects\memdrv\ntoskrnl.def` | 手工导入库定义 |
| `C:\Projects\memdrv\mk_probe.bat` | 一键编译脚本 |
| `C:\Projects\内存压缩\temp\MemCompGui.ps1` | GUI（约 426 行） |
| `C:\Projects\内存压缩\temp\Test-MemCompSimple.ps1` | 功能测试脚本 |
| `C:\Projects\内存压缩\temp\Start-MemCompCtl.ps1` | 启动器 |

---

## 7. 当前状态

```
驱动     : MemCompCtl (Running)
设备     : \\.\MemCompCtl (可用)
压缩     : MemoryCompression=True, PID 4472
可用物理 : 9500 MB
testsigning : Yes
```

**驱动支持 `sc stop` 热卸载**（有正确的 `DriverUnload`），可以热替换而不需重启。

---

## 8. 使用方式

### 加载驱动

```
sc create MemCompCtl type= kernel start= demand binPath= "C:\Projects\memdrv\memcompctl_f.sys"
sc start MemCompCtl
```

### 启动 GUI

```
powershell -ExecutionPolicy Bypass -File "C:\Projects\内存压缩\temp\MemCompGui.ps1"
```

### GUI 界面

- **顶部**：驱动连接状态、系统内存、当前档位、压缩进程 WS
- **优先级区**：5 档单选 + 应用按钮
- **主动压缩区**：执行一次 / 连续 N 次 / 定时自动（间隔可调）
- **底部**：刷新 / 恢复默认

---

## 9. 已知限制

### 9.1 当前档位由 GUI 记录

驱动不提供查询接口（按你的要求），所以 GUI 重启后显示"未知（本次会话未设置）"，而不是假装知道。

这是设计取舍：**要么驱动多一个查询接口，要么 GUI 记状态**。按你的要求选了后者。

### 9.2 Trim 的实际效果无法用页数验证

`SmStTrimWsStore` 里的 `Ws` 是 **Working Set**，它 trim 的是压缩进程的工作集映射，不是压缩存储里的页数。

压缩存储的页数（`[store+0x1B64]`）是**容量**，不会因 Trim 而减少。所以"每次回收 16 MB"这个数字来自 WS 变化，不是页数变化。

**Trim 的真实作用是减少压缩进程的工作集占用**，这本身是有效的内存回收，但不等于"压缩存储里的页数变少"。

### 9.3 设备名残留

如果驱动异常停止（未走 `DriverUnload`），设备名会被残留占用，新驱动加载报错误 183。

**正常 `sc stop` 会正确清理**（已验证），只有强制终止才会残留。

---

## 10. 与前几轮的区别

| 轮次 | 做了什么 | 状态 |
|---|---|---|
| 1-2 | WMI / 内核 API 尝试 | 无效 |
| 3-4 | 写 `[+0x7D0]` bit 0x20 | ✅ 压缩停止接收新页 |
| 5 | 调 `UpdateMemoryCondition` | ✅ 节流 1→16 |
| 6 | `SmpKeyedStoreCreate` 单进程 | ❌ 门禁挡住 |
| 7 | `sub=0x15` + `KeStackAttachProcess` | ✅ **主动 Trim 成功** |
| 8 | 驱动化 + GUI | ✅ **本轮完成** |

**本轮把前 7 轮的成果封装成了可用的工具。**
