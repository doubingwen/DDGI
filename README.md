# Unity URP DDGI

基于 Unity URP 的实验性动态漫反射全局光照项目。使用硬件光线追踪采集 Probe 周围的场景信息，通过 Compute Shader 计算 Radiance、更新八面体映射 Atlas，并在运行时插值重建间接光照。

本项目用于学习和验证 DDGI 的核心流程，不依赖 NVIDIA RTXGI SDK。仓库保留了早期的 SH Probe GI 方案，DDGI 使用独立的代码、Shader 和 Renderer Feature。

## 已实现

- 规则 Probe Volume，默认为 `8 x 8 x 8`，每个 Probe 默认采样 64 根射线。
- 斐波那契球面采样与逐帧射线方向旋转。
- `RayTracingShader` 场景求交，生成 Position、Normal、Albedo 等 Ray G-Buffer。
- 捕获使用 Renderer 的渲染矩阵与 `subMeshStartIndex`，兼容静态合批后的子网格范围；CPU 参考检查可执行 `Tools/Test-DDGISubMeshRanges.ps1`。
- 在命中点计算 Blinn-Phong 直接光照，主方向光通过 URP ShadowMap 判断阴影。
- 采样上一帧 DDGI Volume，为命中点增加漫反射间接光，支持跨帧多次反弹反馈。
- ProbeBlend：每个 Probe 保存 `6 x 6` Irradiance 和 `14 x 14` 距离矩，扩展一圈边界后分别占用 `8 x 8`、`16 x 16` Atlas tile。
- 使用三线性插值、方向权重及切比雪夫可见性权重混合周围 8 个 Probe，并加入 Surface Bias 减少自遮挡。
- 时间累积：Irradiance 使用 gamma 空间混合，距离与距离平方使用相同权重线性混合。
- 运行时逐帧更新、编辑器 Scene 视图预览，以及纹理和间接光调试视图。
- 多 Volume 合成：按世界空间 Probe 密度排序，通过边界线性淡出和剩余覆盖率混合不同分辨率的 Volume。
- Probe 状态调度：关闭静态实体内部的 Probe，休眠空旷区域的 Probe；动态物体新旧位置的影响区域重新检查，激活首帧跳过历史混合。

## 整体流程

```text
Probe Volume / 场景加速结构
    -> 几何包围盒与 Probe 状态调度
    -> 旋转斐波那契射线并与场景求交
    -> Ray G-Buffer（横轴为射线，纵轴为 Probe）
    -> 根据命中距离与背面比例初始化/重新分类 Probe
    -> 直接光照与主光阴影 + 上一帧 DDGI 间接光
    -> Ray Radiance
    -> ProbeBlend：八面体 Irradiance / 距离矩
    -> 历史混合与边界填充
    -> 8 Probe 可见性加权插值
    -> 漫反射间接光合成
```

时间累积与多次反弹反馈是两个不同步骤：前者稳定 Probe 的采样结果，后者使用上一帧 Volume 估算命中点接收到的间接光。

### 跨帧多次弹射

复用的是上一帧 Volume 的 **Irradiance Atlas 和距离矩 Atlas**，不是直接混合上一帧的 Ray Radiance 贴图。对于本次射线命中的表面点 P，使用上一帧的 Probe 插值和可见性测试估算间接辐照度，再通过 Lambert 漫反射计算反射光：

```text
本帧 Ray Radiance(P) = 直接光照(P，含主光阴影)
                     + Albedo(P) / PI × 上一帧 Irradiance(P) × Indirect Bounce Intensity
```

新的 Ray Radiance 再通过 ProbeBlend 更新本帧 Volume，使间接光逐帧传播并近似多次弹射，而非在单次射线中递归追踪多次。首帧没有有效 Volume 历史时不加入该反馈；重新激活的 Probe 也会跳过失效历史。该流程是跨帧迭代近似，不等同于完整的路径追踪。

## 使用方法

项目使用 **Unity 6000.0.67f1 / URP 17.0.4**。光线追踪捕获需要 Windows、Direct3D 12，以及支持 Unity Ray Tracing Shader 的显卡和驱动。

大型旧 GI 烘焙资产通过 Git LFS 管理。安装 Git LFS 后克隆项目：

```bash
git lfs install
git clone https://github.com/doubingwen/DDGI.git
cd DDGI
git lfs pull
```

1. 用 Unity Hub 打开项目，加载 `Assets/Scenes/SampleScene.unity`。
2. 确认 Windows 图形 API 使用 Direct3D 12；修改图形 API 后需要重启 Unity。
3. 使用 URP **Deferred Renderer**，开启 **Compatibility Mode（关闭 Render Graph）**，并启用 `DDGI Composite` Renderer Feature。
4. 检查 `DDGI Probe Volume` 的捕获、Radiance 和 ProbeBlend Shader 引用，确保 Probe 网格覆盖需要计算 GI 的区域。
5. 开启主方向光阴影，并保证相机 Shadow Distance 能覆盖测试区域。关闭旧方案的 `Radiance Field Update` 与 `Radiance Field Composite` Feature，避免两套 GI 叠加。
6. 勾选 `Capture Volume Every Frame` 后进入 Play，即可逐帧更新。编辑器预览还需勾选 `Capture Volume In Edit Mode` 并打开 Scene 视图，空闲预览约每秒刷新 10 次。

关闭逐帧捕获时，Play 仍会在第一个有效 Game 相机渲染时初始化一次 Volume，但不会继续动态更新或累积更多样本。方向光的平移不改变照明，应通过旋转方向光测试动态响应。

## 时间累积参数

| 参数 | 默认值 | 作用 |
| --- | --- | --- |
| Rays Per Probe | 64 | 每个 Probe 每次更新发射的射线数 |
| Enable Temporal Accumulation | 开启 | 混合本次结果与历史 Probe 数据 |
| Rotate Ray Directions | 开启 | 累积开启时逐帧改变球面采样方向 |
| Irradiance Hysteresis | 0.97 | Irradiance 历史权重，越大越稳定，但光照响应越慢 |
| Distance Hysteresis | 0.9 | 距离矩历史权重 |
| Temporal Gamma | 5 | Irradiance 混合指数；设为 1 时采用线性混合 |
| Indirect Bounce Intensity | 1 | 上一帧间接光参与下一次 Radiance 计算的强度 |
| Indirect Diffuse Intensity | 1 | 最终间接光合成强度 |

Irradiance 使用以下混合公式，存回 Atlas 的结果仍为线性值：

```text
new = [(1 - alpha) * current^(1 / gamma) + alpha * previous^(1 / gamma)]^gamma
```

首帧直接使用当前结果，不混合空历史。资源或 Volume 布局变化时历史会失效，也可通过 Inspector 的 `Reset Temporal History` 手动重置。较大的历史权重仍可能产生拖尾，gamma 混合不等于完整的历史拒绝算法。

## 调试

在 Volume Inspector 中可以查看 `Recorded GI Updates`、`Accumulated Frames` 和 `Composite Runtime Status`，以及 G-Buffer、Radiance、主光可见性、Irradiance 和距离矩纹理。`IndirectOnly` 可以隔离观察间接光。

下面是早期单 Probe 捕获的调试图片，采用当时的 144 根射线配置，**不是最终 DDGI 场景效果图**：

| Albedo | Normal | Position |
| --- | --- | --- |
| ![单 Probe Albedo 捕获](Assets/DDGI/Captures/SingleProbe_Albedo.png) | ![单 Probe Normal 捕获](Assets/DDGI/Captures/SingleProbe_Normal.png) | ![单 Probe Position 捕获](Assets/DDGI/Captures/SingleProbe_Position.png) |

## 多 Volume

可以同时创建多个 `DDGI Probe Volume`。例如，大范围 Volume 使用间距 2 的 Probe，局部区域使用间距 1 的 Probe；各自开启逐帧捕获，并生成自己的网格与 Atlas。

- CPU 按 `1 / (世界变换体积缩放 x ProbeSpacing.x x ProbeSpacing.y x ProbeSpacing.z)` 从高密度到低密度排序，包含 Transform 缩放的影响。
- `Boundary Blend Distance` 控制 Volume 内侧的世界空间线性淡出宽度，默认 1。局部 Volume 的淡出带应被低密度 Volume 覆盖，宽度应小于最短边长度的一半。
- 高密度 Volume 优先，后续 Volume 的有效权重为 `remainingCoverage x boundaryWeight`，不是直接把多份 GI 相加。
- 最外层 Volume 在边界淡出到零；归一化后仍保留覆盖率，避免淡出被抵消。
- 各个 Volume 独立进行历史累积与多次反弹反馈；当前没有跨 Volume 查询命中点的上一帧间接光。统一调试模式由密度最高、已有有效数据的 Volume 决定。
- Inspector 的 `Rendered Volume Count` 可以确认参与合成的数量。Volume 每个轴至少设置 2 个 Probe，才能形成有体积的三维采样区域。

CPU 参考混合测试可执行 `pwsh -File Tools/Test-DDGIVolumeBlending.ps1`；该测试验证权重和边界公式，不代替 GPU 画面验证。

### SampleScene 布局

Sponza 的实际 Renderer 包围范围约为 `37.5 x 16.4 x 23.3` 米。示例场景使用两个嵌套 Volume，Transform 均为单位缩放、无旋转：

| Volume | 世界坐标中心 | Probe 网格 | 间距 | 覆盖尺寸 | 边界淡出 |
| --- | --- | --- | --- | --- | --- |
| DDGI Volume - Building | (0.7, 6.0, -0.6) | 8 x 6 x 6，288 个 | (6, 4, 6) | 42 x 20 x 30 米 | 1 米 |
| DDGI Volume - Interior | (0.6, 3.5, -0.3) | 16 x 7 x 9，1008 个 | (2, 2, 2) | 30 x 12 x 16 米 | 1.5 米 |

Building 负责整体覆盖，Interior 提高中庭、走廊及主要室内空间的采样密度；上方屋顶和外围由 Building 补足。两个 Volume 均开启逐帧更新，总计 1296 个 Probe，全量更新为 82944 根射线；状态调度后的实际数量以 Inspector 为准。此布局尚未做 GPU 帧时间标定，不代表比原来的单 Volume 更快。固定建筑需标记 Static 才能避免被状态机当作动态几何持续唤醒。

## Probe 状态机

默认开启 `Enable Probe Classification`。固定的 MeshRenderer 物体需要在 Unity 中标记 **Static**；未标记的物体按动态几何处理。状态存放在 GPU `uint2` Buffer：`x` 是状态，`y` 是更新、历史重置等标志；不包含尚未实现的重定位偏移。

| 状态 | 行为 |
| --- | --- |
| Uninitialized | 发射初始化射线，随后根据结果分类 |
| Off | 保守判定在静态实体内部；不参与间接光插值 |
| Sleep | 周围没有需要捕获的近邻表面；保留 Atlas，跳过常规捕获与更新 |
| Awake | 动态几何包围盒附近；每次 Volume 更新时捕获和计算光照 |
| Vigilant | 附近存在静态表面；持续更新以响应光源变化 |
| NewAwake / NewVigilant | 首次激活时只使用当前采样，不混合旧历史；下一次更新转入常规状态 |

- 几何影响半径默认覆盖一个 Probe cell 的最长世界空间对角线，`Probe Influence Radius Scale` 可以扩大该范围。动态包围盒只用于保守唤醒，不用于判定实体内部。
- 默认要求至少 95% 的全部射线命中背面，且附近没有动态几何，才将 Probe 判定为 `Off`。这是假设法线朝外的封闭网格的启发式判据，不是严格的点在实体内部测试；薄片、反向法线和开放网格需要检查分类结果。
- 动态几何的当前包围盒持续更新；移动、禁用、移除等变化会检查新旧包围盒附近的 Probe。动态物体离开后重新分类，必要时重新初始化该 Probe 的历史。
- 休眠和关闭的 Probe 默认每 64 次 Volume 更新错峰复查一次，避免分类错误永久保留。光源变化不会让附近静态表面的 Probe 休眠。
- Capture、Radiance 和 ProbeBlend 都检查更新标志；休眠 tile 不会每帧清空。重新激活的 Probe 首帧也不会把失效历史用于多次反弹反馈。
- Inspector 的 `Probe State Statistics (Async GPU)` 每约 0.5 秒显示各状态数量及计划更新的 Probe/射线数。`Reclassify Probes` 可请求全部重新分类；关闭分类开关可恢复全量更新进行对比。
- Gizmo：灰色 Off，暗黄色 Sleep，橙色 Awake，绿色 Vigilant，紫色 NewAwake，红色 NewVigilant，白色 Uninitialized。

状态调度减少实际 `TraceRay` 调用及 Radiance/ProbeBlend 工作量，但当前仍按完整网格 Dispatch，并仍扫描场景和重建加速结构，不代表已实现活动 Probe 压缩或增量 AS 更新。CPU 参考状态测试可执行 `pwsh -File Tools/Test-DDGIProbeStates.ps1`；GPU 分类和性能仍需在 Unity 场景中验证。

### 性能验证

场景几何静止并不意味着 Probe 应设为 `Off`：靠近静态表面的 `Vigilant` 仍持续更新，以响应灯光变化。只有实体内部或远离表面的 Probe 才可能跳过常规更新，因此表面密集的 Sponza 场景不保证获得明显的帧数提升。

对比性能时，应保持相同相机、Volume 布局、射线数和显示设置，等待初始化完成后查看 Inspector 的计划更新数量，并用 Unity Profiler 比较启用与关闭分类时的 CPU/GPU 帧时间。当前状态调度仍有以下固定开销：

- 每个 Volume 独立扫描几何、重新添加光追实例并构建加速结构。
- 状态判定与包围盒上传、完整历史 Atlas 复制。
- 全网格 GPU Dispatch 和每个 Volume 的全屏间接光合成。

若多数 Probe 仍为 `Vigilant` 或 `Awake`，跳过的工作量可能不足以抵消调度开销。活动 Probe 索引压缩、静态加速结构复用和更新预算尚未实现，本项目不宣称已获得特定性能提升。

Windows 下可执行 `pwsh -File Tools/Test-DDGIShaderCompilation.ps1` 检查四个 Compute Kernel 的 SM5 编译兼容性。ProbeBlend 的线程组同步不受更新状态分支控制，休眠 Probe 只跳过计算和写入，不提前退出同步流程。

## 当前限制

- ShadowMap 阴影目前仅接入主方向光；点光源和聚光灯虽然参与 Radiance 计算，但尚未接入对应阴影。
- 主光 ShadowMap 由相机生成，覆盖范围受相机级联影响；超出有效级联的 Probe 命中点不会获得主光直接贡献。
- 每次更新扫描 MeshRenderer 并重建加速结构，尚未实现增量更新、Probe 更新预算和 SkinnedMeshRenderer 捕获。
- 尚未实现 Probe 重定位和滚动 Volume。多 Volume 逐个绘制全屏合成，并独立重建加速结构，更新与合成开销随 Volume 数量增加，尚未加入裁剪和共享加速结构。
- 实验性 Blinn-Phong 光照和辐照度近似尚未完成统一的物理能量标定，也没有完整的突变历史拒绝策略。
- 64 根射线的稳定性、漏光、动态拖尾及实际 GPU 性能仍需结合具体场景验证。
