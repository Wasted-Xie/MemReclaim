using System.Reflection;
using System.Runtime.InteropServices;

// 程序集版本信息。
//
// 为什么需要这个文件：原先源码里没有任何 Assembly* 声明，
// 编译出的 exe 版本号是 0.0.0.0、产品名为空。
// 这会导致两个问题：
//   1. 右键 exe → 属性 → 详细信息 里看不到版本，用户无法确认拿到的是哪一版
//   2. 发布 Release 时标签（如 v0.1.0）与 exe 内嵌版本不一致，难以追溯
//
// 版本号约定：
//   AssemblyVersion           —— 供 CLR 绑定使用，只在破坏性变更时递增主版本
//   AssemblyFileVersion       —— 文件版本，对应 Release 标签
//   AssemblyInformationalVersion —— 展示用，可含预发布标识

[assembly: AssemblyTitle("内存回收 (MemReclaim)")]
[assembly: AssemblyDescription("实时回收待机列表、待机列表（无优先级）、系统文件缓存，并定时合并内存列表")]
[assembly: AssemblyProduct("MemReclaim")]
[assembly: AssemblyCompany("Wasted-Xie")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Wasted-Xie")]
[assembly: AssemblyConfiguration("Release")]

[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: AssemblyInformationalVersion("0.1.0")]

// 本程序不使用 COM，明确声明以便 CLR 跳过相关探测
[assembly: ComVisible(false)]
