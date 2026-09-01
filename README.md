# PackLab — 3D 自动装箱 MVP

一个可直接运行的 ASP.NET Core Web 应用。后端实现实际三维尺寸、6 向旋转、碰撞/边界检查、底面支撑与重心稳定性检查、多箱拆分及自动箱型选择；前端使用 Three.js 只负责渲染后端返回的坐标。

## 启动

需要 .NET 9 SDK（项目没有 Node.js 构建步骤）。

```powershell
dotnet run
```

然后访问 <http://localhost:5000>。

## 测试

```powershell
dotnet test BinPacking.sln
```

测试覆盖旋转、箱体边界、商品重叠、承重、支撑稳定性、多箱拆分、EMS waste trap 和多箱前瞻 trap。

Release benchmark（固定 seed，8 类场景 × 6/10/20/50/100/200 件）：

```powershell
dotnet run --project benchmarks/BinPacking.Benchmarks/BinPacking.Benchmarks.csproj -c Release -- benchmarks/results/current.json hybrid
```

结果包含装箱时间、首箱与总体利用率、箱数、总体箱体积、未装件数、候选评估数、近似线程分配量以及 p50/p95。`benchmarks/results/baseline.json` 保存优化前基线。

## 目录

- `Algorithms/IPackingAlgorithm.cs`：可替换算法契约
- `Algorithms/ExtremePointPackingAlgorithm.cs`：保留并热点优化的兼容算法，也是 benchmark baseline 对照路径
- `Algorithms/HybridPackingAlgorithm.cs`：Enhanced Extreme Point + EMS + adaptive waste/future-fit scoring + 按需有限 Beam Search/记忆化
- `Algorithms/EmptySpaceManager.cs`：EMS difference、canonicalization、包含/重复/不可用空间 pruning 和数量上限
- `Algorithms/ExtremePointManager.cs`：商品边缘/EMS 边界投影点生成与合法性 pruning
- `Algorithms/PackingAlgorithmOptions.cs`：Fast、Balanced、Quality 模式和所有可调权重/预算
- `Services/BoxSelectionService.cs`：同箱型与混合箱型方案比较、多箱分配
- `Services/BoxPlanOptimizer.cs`：箱型预筛选、体积/重量 lower bound 和深度 2 的多箱前瞻
- `Services/CatalogStore.cs`：线程安全的 MVP 内存数据仓库（启动时载入示例数据）
- `Controllers/`：箱型、商品与装箱 REST API
- `wwwroot/`：无构建步骤的管理界面、Three.js 透视图和装箱动画
- `tests/BinPacking.Tests/`：算法不变量测试

## 坐标约定

后端统一使用 `X = Length`、`Y = Width`、`Z = Height`。Three.js 渲染时仅做坐标轴映射：后端 Z 映射为屏幕竖直轴，几何尺寸和位置不重新计算。

## 搜索模式与安全约束

- `Fast`：关闭 Beam Search，Top-K/EMS/极值点上限较小，适合低延迟请求。
- `Balanced`：默认宽度 4、分支 4、深度 2、Beam 时间预算 250ms；容易订单走快速路径，困难状态才触发前瞻。
- `Quality`：宽度 8、分支 5、深度 4、预算 1200ms；小于等于 6 件时将深度扩展到近似穷举范围。

模式和权重可在 `appsettings.json` 的 `PackingAlgorithm` 节点调整。边界、碰撞、承重、重心支撑、最低 90% 底面支撑率和最低 70% 四象限支撑率始终是 hard constraints；后两个阈值只有显式配置才会改变。

> MVP 的箱型与商品 CRUD 数据存于进程内存，应用重启后恢复内置示例数据。若用于生产，可在不改 API 和算法的前提下将 `CatalogStore` 替换为 EF Core 持久化实现。

## CI/CD 与服务器部署

推送到 `main` 后，GitHub Actions 会依次执行 Release 编译、测试、SSH 上传、服务器 Docker 构建和健康检查。生产容器配置如下：

- 容器名：`box-packing-app`
- 容器端口：`8080`
- Docker 网络：`box-packing-network`
- 重启策略：`unless-stopped`
- 健康检查：`/health`
- 服务器发布目录：`$HOME/apps/3d-bin-packing/releases/<commit>`

仓库需要以下 Repository Secrets：

- `SERVER_HOST`
- `SERVER_PORT`
- `SERVER_SSH_KEY`
- `SERVER_USER`

首次部署后，将 Nginx 容器接入应用网络：

```bash
docker network connect box-packing-network nignx
```

之后在 Nginx 中将 `box.junhoo.com` 反向代理到：

```text
http://box-packing-app:8080
```

部署失败时，工作流会删除失败的新容器并恢复上一版容器。也可以在 GitHub Actions 页面使用 `workflow_dispatch` 手动部署。
