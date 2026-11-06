# Ring0 接管内存压缩：成功验证报告

> **日期**：2026-11-03
> **目标**：从 Ring0 直接修改 `SM_PARTITION+0x7D0` 的 bit 0x20（压缩功能标志）
> **结果**：**成功翻转，成功验证效果，成功恢复原状**

---

## 1. 最终答案（一句话）

**改这个 bit 确实能让压缩存储停止接收新页，但它不能释放已有的压缩内存，也不会让 `Memory Compression` 进程退出。**

---

## 2. 完整实验数据

### 2.1 目标定位（全部命中）

| 项 | 值 |
|---|---|
| 进程名 | **`MemCompr`**（EPROCESS 里的名字，非显示名） |
| PID | **4460** |
| EPROCESS | `0xFFFFD10FFB307040` |
| Partition 对象 | `0xFFFFD10FC46C3F40` |
| **SM_PARTITION** | **`0xFFFFD10FC4ACE010`** |

### 2.2 位翻转（第一次写）

| 阶段 | 值 |
|---|---|
| `FlagsBefore` | **`0x7B`** = `0111 1011` → bit 5 (0x20) = **1** |
| 安全检查 | ✅ 通过（flags 非 0 且 bit 0x20 已置位） |
| `FlagsAfter` | **`0x5B`** = `0101 1011` → bit 5 = **0** |
| 读回验证 | ✅ 确认 `0x5B` |

**无蓝屏，无异常。**

### 2.3 位状态持久性（2 分钟后二次读取）

| 项 | 第一次 | 第二次 |
|---|---|---|
| `SM_PARTITION` | `0xFFFFD10FC4ACE010` | **同一地址** |
| `FlagsBefore` | `0x7B` (bit=1) | **`0x5B` (bit=0)** |
| 安全检查 | 通过 | **拒绝**（位已清除，不重复写） |

**结论：位翻转是持久的，内核没有自动恢复它。**

### 2.4 行为验证（关键）

**位清除前（bit=1）加压 8 GB：**
```
压缩进程 WS:  0 MB → 81.1 MB   （增长）
Peak:         86.9 MB
```

**位清除后（bit=0）加压 8 GB：**
```
基线 WS:      80.8 MB  Peak 86.9 MB
加压中 WS:    80.8 MB  Peak 86.9 MB   ← 完全不变
可用物理:     1145.7 MB
已提交:       17.9 GB
```

**结论：bit 0x20 清除后，压缩存储确实停止接收新页。**

### 2.5 恢复（第二次写）

| 阶段 | 值 |
|---|---|
| `FlagsBefore` | `0x5B` (bit=0) |
| 恢复操作 | 置位 bit 0x20 |
| `FlagsAfter` | **`0x7B`** (bit=1) |
| 读回验证 | ✅ 与初始值完全一致 |

**系统已恢复原状。**

---

## 3. 为什么"关掉压缩"不能释放内存

这是本轮最重要的认知：

| 误解 | 实际 |
|---|---|
| 关掉压缩 → 压缩内存被释放 | ❌ 已压缩的页原样留在存储里 |
| 关掉压缩 → 进程退出 | ❌ `Memory Compression` 进程仍在，PID 不变 |
| 关掉压缩 → 可用内存增加 | ❌ 实测 9341 → 9355 MB，无实质变化 |

**原因**：
- `[+0x7D0]` 的 bit 0x20 只控制**是否允许新页进入压缩存储**
- 已压缩的页需要走 `SmFeEvictInitiate`（RVA `0x2DF8C0`）驱逐流程才会释放
- 而 `SmTrimWsStore` 那条路径因为 store 缺 bit 17（`0x20000`）标志而**永远不可达**

---

## 4. 完整技术链路（四轮累计）

```
用户态
├─ Disable-MMAgent              → 只改 WMI 层标志，压缩存储不动
├─ NtSetSystemInformation(0x6D)
│   ├─ sub=0x13 Config           → 管分区存储配置
│   └─ sub=0x14 Trim             → SmTrimWsStore 因 bit17 未设而拒绝
│
Ring0（本轮）
└─ 写 SM_PARTITION+0x7D0
    ├─ bit 0x20 清除             → ✅ 压缩停止接收新页（已验证）
    └─ 要释放已压缩页            → 需 SmFeEvictInitiate，未尝试
```

---

## 5. 工具链（从零搭建，全部验证可用）

系统**没有 WDK、没有 km 头文件、没有 ntoskrnl.lib**，最后做到了驱动成功加载运行：

| 组件 | 方案 |
|---|---|
| 编译器 | MSVC `cl.exe` 19.44.35229 x64（VS Community 自带） |
| 头文件 | 全部手工声明（`UNICODE_STRING`/`OBJECT_ATTRIBUTES`/`NTSTATUS`） |
| 导入库 | 手写 `.def` + `lib.exe` 生成最小 `ntoskrnl.lib` |
| 链接 | `/DRIVER /SUBSYSTEM:NATIVE,10.00 /ENTRY:DriverEntry /NODEFAULTLIB /FIXED:NO` |
| 签名 | `New-SelfSignedCertificate` + `signtool` |
| 证书信任 | `certutil -addstore Root/TrustedPublisher` |
| 结果回传 | 驱动写注册表（**无需调试器**） |

**踩平的坑（供复现参考）**：
1. `cl.exe` 在含中文路径下 `cd /d` 失败 → 改用 `C:\Projects\memdrv`
2. 当前目录的 `link.bat` 遮蔽 `link.exe` → 改名
3. `vcvars64.bat` 重复调用导致 PATH 溢出 → 只调一次
4. 驱动缺 `.reloc` 节 → 加绝对地址引用强制生成
5. 驱动缺导入表 → 手写 `.def`
6. Subsystem version 6.00 太老 → 指定 `10.00`
7. `__chkstk` 未解析 → 提供空实现
8. `MakeCert` 私钥不可导出 → 改用 `New-SelfSignedCertificate`
9. `signtool` 找不到 `PrivateCertStore` → 装到 `My`
10. 驱动无 `DriverUnload` 无法卸载 → 每次用新文件名

---

## 6. 关键偏移（全部交叉验证）

| 字段 | 偏移 | 验证证据 |
|---|---|---|
| `UniqueProcessId` | `EPROCESS+0x1D0` | 读回 **4**（System 进程） |
| `ActiveProcessLinks` | `EPROCESS+0x1D8` | 读回有效内核指针 |
| `ImageFileName` | `EPROCESS+0x338` | 读回 `"System"` |
| 分区对象 | `EPROCESS+0x758` | `SmpGetProcessPartition` 反汇编 |
| `SM_PARTITION` | `分区对象+0x18` | 同上 |
| 压缩标志 | `SM_PARTITION+0x7D0` | `SmFirstTimeInit` 反汇编 |

**特别发现**：`Memory Compression` 在 EPROCESS 里叫 **`MemCompr`**，且**不挂在 `ActiveProcessLinks` 链表**（是 `PsCreateMinimalProcess` 创建的最小进程），必须走 `PspCidTable`（`PsLookupProcessByProcessId`）才能查到。

---

## 7. 系统当前状态

```
MemoryCompression : True      ← 位已恢复 0x7B
PageCombining     : True
压缩进程          : PID 4460, 72.9 MB
SysMain           : Running
可用物理          : 9355 MB
已提交            : 9.86 GB
运行时长          : 49.6 分钟
```

**回滚状态**：
- ✅ `SM_PARTITION+0x7D0` 已恢复为 **`0x7B`**（与初始值完全一致，读回验证）
- ✅ 14 个测试驱动服务注册已全部删除（重启后不再加载）
- ✅ 测试证书从 Root / TrustedPublisher / My 全部清除（0 残留）
- ⚠️ 驱动实例仍在内存（无 `DriverUnload`），但只在 `DriverEntry` 执行一次，是死代码，**重启即消失**
- ⚠️ `testsigning` 仍为 `Yes`（你开的，保留）

---

## 8. 结论

**Ring0 接管成功了，但效果不如预期。**

- ✅ 能精确定位 `SM_PARTITION`（`0xFFFFD10FC4ACE010`）
- ✅ 能读写 `[+0x7D0]` 的标志位
- ✅ 位翻转持久，内核不会恢复
- ✅ 清除 bit 0x20 后，压缩存储确实停止接收新页（Peak 不再增长）
- ❌ **但已压缩的内存不会释放**
- ❌ **压缩进程不会退出**
- ❌ **可用内存没有实质增加**

**真正的"关闭内存压缩"需要同时做两件事**：
1. 清 bit 0x20（已完成，已验证）
2. 调用 `SmFeEvictInitiate` 驱逐已压缩页（未尝试，复杂度高）

第 2 步需要构造完整的 `T_SM_PAGE_KEY` 并遍历 B+ 树，风险显著高于改一个 bit。**建议到此为止。**
