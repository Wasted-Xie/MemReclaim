# MemCompCtl 开发进展与重启需求

> **日期**：2026-11-04
> **状态**：驱动+GUI 架构完成，IOCTL 全部验证通过，**需重启一次清理僵尸设备对象**

---

## 1. 本轮已完成的验证（全部通过）

### 1.1 全部 IOCTL 实测成功

在重启前（旧驱动 `memcompctl.sys` 正常运行时）的测试结果：

```
GET_STATUS  -> 56 字节
  SM_PARTITION = 0xFFFF8088A3ACE010
  StoreObj     = 0xFFFF8088D507F000
  Flags7d0     = 0x7B   bit20=1
  StoreFlags   = 0x320800  bit17=1
  Throttle     = 1
  StoreIndex   = 0x400
  ProcPid      = 4400

GET_CAPS    -> 32 字节
  NtoskrnlBase = 0xFFFFF803B7800000
  CanTrim=1  CanSetPriority=1  CanSetEnable=1

GET_STATS   -> 320 字节  ★ 关键突破
  Ret           = 0
  IsMainProcess = 1
  EprocessAddr  = 0xFFFF8088D5081040
  Part878       = 0xFFFF8088D5081040    ← 完全相等，验证通过
  TotalPages    = 128208 页 (≈ 500 MB)

SET_PRIORITY:
  cond=0 -> Throttle=3   (最高优先级)
  cond=2 -> Throttle=6
  cond=3 -> Throttle=12
  cond=4 -> Throttle=16  (最低)

ACTIVE_TRIM -> TrimStatus = 0x00000000  (成功)

SET_ENABLE:
  设 0 -> bit20=0
  设 1 -> bit20=1
```

### 1.2 重大突破：压缩存储真实页数可读

`GET_STATS` 解决了「WS 显示为 0 但压缩实际在工作」的观测问题。

**关键数据**：
- 压缩存储有 **128,208 页** ≈ 500 MB
- `WorkingSet64` 显示 0，因为压缩数据走 MDL 映射，不计入工作集
- `IsMainProcess = 1` 证明 MemCompr 就是分区主进程，可直读统计

### 1.3 ST_STATS 结构解析（从 StGetStatsWorker 0x433C80 反汇编）

| 偏移 | 类型 | 含义 | 实测值 |
|---|---|---|---|
| `+0x04` | dword | 结构大小 | `0xBC` = 188 |
| `+0x08` | word | `[store+0x12C0]` | — |
| `+0x0C` | dword | `[store+0x008]` | `0x1F4D0` |
| `+0x10` | dword | `[store+0xCB8]+[store+0x3A8]` | `0x1F4D0` |
| `+0x14` | dword | `[store+0x00C]` | `0x10` |
| `+0x18` | dword | 固定 `0x10` | `0x10` |
| `+0x1C` | dword | `[store+0x3A8]` 主页数 | `0x1F4D0` |

---

## 2. 本轮修复的 Bug

| # | 问题 | 根因 | 修复 |
|---|---|---|---|
| 1 | 启动器误判「创建服务失败」 | 用英文 `SUCCESS` 匹配中文「成功」 | 改用 `$LASTEXITCODE` |
| 2 | `GET_STATS` 返回 -122 | 申请 288 字节，结构实际 320 字节 | 改为 320 |
| 3 | `DriverUnload` 从不执行 | 偏移写成 `0x68`（实为 `DriverStartIo`） | 改为 `0x70` |
| 4 | 分派表写错位置 | `MajorFunction` 起点写成 `0x70` | 改为 `0x78` |

**正确的 DRIVER_OBJECT 布局**（从 `IopLoadDriver` 0x203E87 `lea rsi, [rdi+0x70]` 验证）：

```
DRIVER_OBJECT:
  +0x60  DriverInit
  +0x68  DriverStartIo
  +0x70  DriverUnload        ← 修正后
  +0x78  MajorFunction[0]    ← 修正后
```

---

## 3. 当前阻塞：僵尸设备对象

### 3.1 现象

```
sc.exe start MemCompCtl  ->  FAILED 183 (ERROR_ALREADY_EXISTS)
```

### 3.2 诊断过程

| 检查项 | 结果 |
|---|---|
| 设备 `\\.\MemCompCtl` 能否打开 | ❌ err=2 (不存在) |
| 符号链接 `MemCompCtl` | ❌ NOT FOUND |
| 服务注册 | ✅ 正确（指向 mctl5.sys） |
| 驱动文件被占用 | ❌ 未占用 |
| **换名字 `MemCompCtlTest`** | ✅ **加载成功，STATE=RUNNING** |

### 3.3 结论

**`\Device\MemCompCtl` 这个设备名被一个"已删除但仍被引用"的僵尸设备对象占用了。**

成因链：
1. 早期版本 `memcompctl.sys` 加载后，`DriverUnload` 偏移错误（`0x68`），**卸载例程从未执行**
2. `sc stop` 时内核只是标记驱动停止，但 `IoDeleteDevice` / `IoDeleteSymbolicLink` 没被调用
3. 设备对象残留，处于"名字已占用、对象不可访问"的状态
4. 新驱动 `IoCreateDevice` 尝试同名，返回 `STATUS_OBJECT_NAME_COLLISION` → 183

**这种状态无法通过任何代码清理**，只能重启。

---

## 4. 重启后的预期

重启会：
- 清掉所有僵尸设备对象
- 清掉所有残留驱动实例
- 服务注册已删除，不会自动加载

重启后运行 `Start-MemCompCtl.ps1`，它会：
1. 检查 testsigning ✅
2. 检查驱动文件签名 ✅
3. 加载 `mctl5.sys`（或更新版本）
4. 打开 GUI

**新版驱动有正确的 `DriverUnload`（0x70），之后可 `sc stop` 热替换，不用再重启。**

---

## 5. 文件清单

| 文件 | 说明 | 状态 |
|---|---|---|
| `C:\Projects\memdrv\memcompctl.h` | IOCTL 协议定义 | ✅ 含 GET_STATS |
| `C:\Projects\memdrv\memcompctl.c` | 驱动源码 | ✅ 偏移已修正 |
| `C:\Projects\memdrv\mctl5.sys` | 最新驱动（标准 `\??\` 链接名） | ✅ 已签名 |
| `C:\Projects\memdrv\ntoskrnl.def` | 导入库定义 | ✅ |
| `C:\Projects\memdrv\mk_probe.bat` | 编译脚本（含 `/utf-8`） | ✅ |
| `C:\Projects\内存压缩\temp\MemCompGui.ps1` | GUI 主程序 | ✅ 语法通过 |
| `C:\Projects\内存压缩\temp\Start-MemCompCtl.ps1` | 启动器 | ✅ 退出码判断已修 |
| `C:\Projects\内存压缩\temp\Test-MemCompCtl.ps1` | 驱动测试脚本 | ✅ 全功能覆盖 |

---

## 6. 编译要点（重要）

```
cl /nologo /c /GS- /Gs- /Zl /W3 /Zc:wchar_t- /O1 /utf-8 /Fo<obj> <src>
```

**`/utf-8` 是必需的**，否则中文注释被 GBK 解析，吞掉换行导致大量语法错误。

**必须自己实现 `memset` / `memcpy`**，因为 `/NODEFAULTLIB` 下编译器优化后的调用没有库可链接。

---

## 7. 下一步

**需要你重启一次。**

重启后我继续：
1. 验证 `mctl5.sys` 能正常加载
2. 跑 `Test-MemCompCtl.ps1` 全量测试
3. 验证 GUI 各按钮
4. 用 `GET_STATS` 观察主动 Trim 前后的页数变化（这才是真实效果）

---

## 8. 系统当前状态

```
testsigning : Yes
运行时长    : 6.6 分钟
服务注册    : 已全部清理
残留驱动    : MemCompTestDev (testdev.sys, 内存中，重启消失)
              MemCompCtl     (mctl5.sys, STOPPED)
```

**重启是安全的**：服务注册已删，不会有任何驱动自动加载。
