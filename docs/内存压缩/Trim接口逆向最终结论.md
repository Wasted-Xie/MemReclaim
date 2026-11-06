# Trim 接口参数逆向：最终结论

> **日期**：2026-11-03
> **目标**：解出 `NtSetSystemInformation(0x6D)` → `SmProcessSystemStoreTrimRequest` 的完整参数
> **结论**：**参数已完全解出，但该功能在当前系统上不可达**——不是参数问题，是 store 标志从未被设置

---

## 1. 参数结构（已完整解出并验证）

### 1.1 外层（0x18 字节，必须精确）

| 偏移 | 类型 | 值 |
|---|---|---|
| `0x00` | dword | 必须 = **1** |
| `0x04` | dword | sub-command = **0x14**（Trim） |
| `0x08` | qword | 内层结构指针 |
| `0x10` | qword | 内层长度 |

### 1.2 内层（0x10 或 0x18 字节）

| 偏移 | 类型 | 约束 | 来源 |
|---|---|---|---|
| `0x00` | dword | **flags**，低字节必须 1 或 2；`0xFFFFE000` 位必须为 0 | `0xA78574`–`0xA785C0` |
| `0x04` | dword | 保留 | — |
| `0x08` | qword | **必须非 0** | `0xA785D2` `cmp [rsp+0x40], rdi(=0)` |
| `0x10` | qword | 分区句柄（仅 len=0x18 时） | `0xA785F7` |

**长度规则**：
- `flags` 低字节 == **1** → 内层长度必须正好 **0x10**
- `flags` 低字节 == **2** → 内层长度必须正好 **0x18**

### 1.3 flags 位定义（从 `0xA785C0`–`0xA785F5` 解出）

```
test esi, 0xFFFFE000      ; bit13-31 必须为 0
bt   esi, 8               ; bit8 = 控制位
test esi, 0x1E00          ; bit9-12
```

- **bit 8 (`0x100`) == 0** → 走 `0xA78743`：`SmKmStoreGet` + **`SmTrimWsStore`**（工作集修剪）
- **bit 8 == 1** → 走 `0xA7862B`：另一条路径（`MmUpdateOldWorkingSetPages`）
- bit 9–12 必须 ≤ `0xC00` 对应位

---

## 2. 实测结果

| 构造 | 返回 | 含义 |
|---|---|---|
| `flags=1, len=0x10, val8=0` | `0xC0000059` | `STATUS_INVALID_DEVICE_REQUEST`（`[0x08]` 为 0） |
| `flags=1, len=0x10, val8=1/4096/1048576` | **`0xC00000BB`** | **`STATUS_NOT_SUPPORTED`** |
| `flags=2, len=0x18, handle=0` | `0xC0000059` | 句柄为 0 |
| `flags=0x102, len=0x18` | `0xC0000059` | 同上 |

**所有校验都通过了**（否则不会到 `0xC00000BB`），最终卡在 `SmTrimWsStore`。

---

## 3. 根因：`SmTrimWsStore` 是死代码

### 3.1 函数实现

```
004BEB1C  sub  rsp, 0x28
004BEB20  test dword ptr [rdx], 0x20000     ; store 对象 flags 的 bit 17
004BEB26  mov  r9, rdx
004BEB29  je   0x4BEB3C
004BEB2B  mov  rdx, r8
004BEB2E  mov  rcx, r9
004BEB31  call SmStTrimWsStore              ; 真正的修剪
004BEB36  ...
004BEB3C  mov  eax, 0xc00000bb              ; ★ STATUS_NOT_SUPPORTED
004BEB41  jmp  0x4BEB36
```

**`SmTrimWsStore` 要求 store 对象的 flags 带 bit 17（`0x20000`），否则立即返回 `STATUS_NOT_SUPPORTED`。**

### 3.2 这个标志从未被设置

全内核搜索 `0x20000` 与 bit 17 的所有指令形式：

```
命中 4 处：
  2037F7  bt   dword ptr [rbx + 0x74], 0x11
  203839  btr  edi, 0x11
  203979  bt   dword ptr [rip+0xdc3187], 0x11
  205C4A  add  eax, 0x20000
```

**没有任何一处是 `or`/`bts` 给 store flags 设置 bit 17。**

### 3.3 这意味着什么

`SmTrimWsStore` 在**当前这个内核版本上是不可达的**：

- 不是我的参数不对（参数已经全对，否则到不了 `0xC00000BB`）
- 是**这个功能本身没有启用**
- 微软可能在特定 SKU / 特定配置下才设置该标志，或者是遗留代码

---

## 4. 所以：Trim 这条路走不通

`SmProcessSystemStoreTrimRequest` 的完整调用链：

```
sub-cmd 0x14
  → 校验 flags / len / val8 / handle        ← 全部通过
    → SmpGetProcessPartition                 ← 通过
      → [partition+0x8e0] store 索引          ← 有效
        → SmKmStoreGet                        ← 拿到 store 对象
          → SmTrimWsStore                     ← ★ 检查 bit17，失败
            → 0xC00000BB
```

**卡在最后一步，且这一步在当前内核里永远不会成功。**

---

## 5. 那么"手动进调试模式"能解决吗

能，但需要明确能做什么、不能做什么：

### 5.1 调试模式（WinDbg + 内核调试）能做的

| 能力 | 可行性 |
|---|---|
| 读取 `SMKM_STORE_MGR` 实例地址 | ✅ 可以，用 `!pool` / 断点定位 |
| 读取 `[+0x7d0]` 的实际值 | ✅ 可以 |
| **修改** `[+0x7d0]` 的 bit 0x20 | ✅ 可以（`eb` 命令） |
| 观察修改后行为 | ✅ 可以 |

### 5.2 但要注意

1. **内核调试需要另一台机器**（网络/串口/USB 调试），单机自调试不可行
2. 或改 BCD `debug` + `debugport`，重启进入调试态
3. 修改 `[+0x7d0]` 只是**改一个标志位**，能否真正关闭压缩存储**不确定**——因为 `SmStorePrepare` 已在初始化时跑过
4. 真正要释放 370 MB，需要触发 `SmFeEvictInitiate` 驱逐流程，那需要构造完整的 `T_SM_PAGE_KEY` 并调用内核函数，复杂度远超改一个 bit

### 5.3 我的建议

如果你要进调试模式，**最有价值的实验是**：

```
1. 定位 SMKM_STORE_MGR 实例地址
2. 读出 [+0x7d0] 的值，确认 bit 0x20 是否为 1
3. 用 eb 把 bit 0x20 清零
4. 观察 Memory Compression 进程工作集是否变化
5. 观察系统是否稳定
```

这个实验**风险可控**（改单个 bit，重启即恢复），且能给出确定性答案：**改这个 bit 到底有没有用**。

比写驱动安全得多——因为不落盘、不改 BCD、重启即恢复。

---

## 6. 本轮改动：零

```
MemoryCompression : True           未变
压缩进程          : PID 4020, 341.5 MB  未变
SysMain           : Running         未变
testsigning       : No             未改
新增驱动服务      : 无              未加
磁盘剩余          : 197.35 GB
```

---

## 7. 三轮下来完整的认知地图

| 层 | 接口 | 状态 |
|---|---|---|
| WMI | `Disable-MMAgent` | ✅ 可调用，但只改浅层标志，压缩存储不动 |
| 内核 API | `NtSetSystemInformation(0x6D, sub=0x13)` Config | ✅ 可调用，管分区配置，不是压缩开关 |
| 内核 API | `NtSetSystemInformation(0x6D, sub=0x14)` Trim | ❌ 参数全对，但 `SmTrimWsStore` 因 bit17 未设而拒绝 |
| 内核内存 | `SMKM_STORE_MGR+0x7d0` bit 0x20 | ⚠️ 需要调试器才能读写，效果未验证 |
| 驱动 | 自写驱动改标志 | ⚠️ 需改 BCD + 重启 + 绕过火绒 |

**真正的开关是 `[+0x7d0]` 的 bit 0x20，它在 Store Manager 初始化时一次性写入，运行时所有官方接口都碰不到它。**

要验证改它有没有用，只有调试器这条路。
