# TextCascade v2.3.0.0 运行性能报告 / Runtime Performance Report

- 采集日期 / Date: 2026-08-28
- 构建版本 / Build: 2.3.0.0(commit `471328a`,tag `v2.3.0.0`)
- 运行环境 / Environment: Windows 11 x64,12 逻辑核,框架依赖部署(`SelfContained=false`,win-x64,需 .NET 10 桌面运行时)
- 进程 / Process: PID 37088,`TextCascade.exe`,启动 2026-08-28 13:22:25,采集时已连续运行约 6.07 小时
- 采集方式 / Method: PowerShell 进程快照 + 20 秒 CPU 两点采样;`dotnet-counters`(EventPipe,`System.Runtime`,5 秒刷新,40 秒窗口);应用日志 `%APPDATA%\TextCascade\TextCascade.log` 全量核查

---

## 中文

### 进程级指标(运行 6.07 小时)

| 指标 | 数值 | 说明 |
|---|---|---|
| 累计 CPU 时间 | 19.1 秒 | 折合平均 ≈0.09% 单核(12 逻辑核),常驻成本可忽略 |
| 实时 CPU 占用(20 秒采样) | 0% | 12 逻辑核归一,采样窗口内完全静默 |
| 工作集 | 100.9 MB | 大部分为共享框架映像与内存映射 |
| 私有内存 | 29.8 MB | 进程真实独占占用 |
| 虚拟内存 | ≈2 PB(地址空间保留) | x64 常态,无实际意义 |
| 线程数 | 15 | 20 秒窗口内零漂移 |
| 句柄数 | 939 | 20 秒窗口内零漂移,无泄漏迹象 |
| 优先级 | Normal | — |

### .NET 运行时指标(40 秒 EventPipe 采集)

| 指标 | 数值 |
|---|---|
| 托管 GC 堆 | ≈1.35 MB(gen1 545 KB + gen2 572 KB + LOH 197 KB + POH 24 KB;gen0 几乎为空) |
| GC 提交内存 | 7.2 MB |
| 采样窗口 GC 次数 / GC 暂停 | 0 次 / 0 秒 |
| 稳态分配速率 | ≈25–33 KB/s(心跳与剪贴板轮询 timer 的常规小分配) |
| 线程池 | 队列长度 0;工作项 ≤3/5 秒;锁竞争 0;常驻 timer 仅 2 个 |
| JIT | 空闲期后台 tiered 编译冷路径(53–117 方法/5 秒),属正常预热 |

### 稳定性证据

- 当前实例 6 小时日志**零条目**(Logger 仅记录警告/错误,即运行期间无任何异常)。
- 日志中的历史条目(一次 WebSocket 子协议协商 400、13:16:57 `server_shutdown` bye)均来自重启前的旧会话,与本次采集实例无关。
- 句柄、内存、线程三项在 20 秒采样窗口内零漂移,无泄漏或失控迹象。

### 结论

应用处于极安静的常驻空闲态:平均 CPU 不足千分之一核,托管堆仅 ~1.4 MB,无锁竞争、无异常、无资源漂移。v2.3.0.0(移除自研 GcmCipher、改用内置 AesGcm)在真实长时运行下未引入可观测的额外开销。

---

## English

### Process-level metrics (6.07 hours uptime)

| Metric | Value | Notes |
|---|---|---|
| Total CPU time | 19.1 s | ≈0.09% of one core on average (12 logical cores); negligible for a resident tray app |
| Live CPU usage (20 s sampling) | 0% | Normalized by 12 logical cores; fully silent during the window |
| Working set | 100.9 MB | Mostly shared framework images / memory mappings |
| Private memory | 29.8 MB | Actual exclusive footprint |
| Virtual memory | ≈2 PB (reserved) | Normal x64 address-space reservation |
| Threads | 15 | Zero drift in the 20 s window |
| Handles | 939 | Zero drift in the 20 s window; no leak indication |
| Priority | Normal | — |

### .NET runtime metrics (40 s EventPipe collection)

| Metric | Value |
|---|---|
| Managed GC heap | ≈1.35 MB (gen1 545 KB + gen2 572 KB + LOH 197 KB + POH 24 KB; gen0 nearly empty) |
| GC committed memory | 7.2 MB |
| GC collections / GC pause in window | 0 / 0 s |
| Steady-state allocation rate | ≈25–33 KB/s (routine small allocations from heartbeat and clipboard polling timers) |
| Thread pool | Queue length 0; ≤3 work items / 5 s; zero lock contentions; 2 resident timers |
| JIT | Background tiered compilation of cold paths (53–117 methods / 5 s); normal warm-up behavior |

### Stability evidence

- Zero log entries in 6 hours for the running instance (the Logger only records warnings/errors, i.e. no exceptions occurred).
- Historical log entries (one WebSocket subprotocol negotiation 400, a `server_shutdown` bye at 13:16:57) belong to the session before the restart, not to the instance measured here.
- Handles, memory and threads showed zero drift during the 20 s sampling window — no leak or runaway behavior.

### Conclusion

The app idles extremely quietly: under 0.1% of a single core on average, a ~1.4 MB managed heap, no lock contention, no exceptions and no resource drift. v2.3.0.0 (self-implemented GcmCipher removed, built-in AesGcm adopted) introduces no observable overhead in real long-running usage.

---

## 复现方式 / Reproduction

```powershell
# 进程快照 / process snapshot
Get-Process -Name TextCascade | Select-Object Id, StartTime, TotalProcessorTime, Threads, Handles, WorkingSet64, PrivateMemorySize64

# CPU% 两点采样 / two-point CPU sampling (normalized by logical cores)
# (delta TotalProcessorTime) / (wall time x logical cores) x 100

# .NET 运行时计数器 / .NET runtime counters
dotnet tool install -g dotnet-counters
dotnet-counters collect -p <PID> --counters System.Runtime --refresh-interval 5 --format csv -o tc-counters
```
