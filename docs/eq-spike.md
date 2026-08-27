# EQ 技术验证 Spike Report

**日期**: 2026-08-27  
**结论**: **⚠️ CONDITIONAL GO — AudioGraph 路径可行，但需要重构播放管道**

---

## Executive Summary

| 问题 | 结论 |
|------|------|
| `EqualizerEffectDefinition` 是系统内置效果吗？ | ✅ 是 — `Windows.Media.Audio` 命名空间，封装 XAudio2 FXEQ XAPO |
| 支持自定义中心频率吗？ | ✅ 是 — `Bands[i].FrequencyCenter` 可设置（20 Hz – 20 kHz） |
| 最大频段数？ | **每实例 4 段** — 可堆叠多个 `EqualizerEffectDefinition`（已知有人用 3 个实现 9 段） |
| `MediaPlayer.AddAudioEffect` 可用吗？ | ❌ **不可用** — `EqualizerEffectDefinition` 仅限 AudioGraph |
| AudioGraph 能接受 `MediaSource` 吗？ | ✅ 是 — `CreateMediaSourceAudioInputNodeAsync`（Win10 1803+） |
| 利润范围？ | 0.126–7.94 线性振幅（-18dB 到 +18dB），**不能归零** |

**判定：GO 条件成立** — AudioGraph 可以接管播放管道并应用系统内置 EQ，但需要将 `PlaybackService` 从 `MediaPlayer` 迁移到 `AudioGraph`。这是中等工作量（1-2 天），不是重写。

---

## 1. 现有架构分析

### 当前播放管道

```
PlaybackService._player (MediaPlayer, headless)
    ├── MediaSource.CreateFromStorageFile()  ← 原生格式（mp3/wav/flac...）
    ├── FFmpegMediaSource.CreateFromUriAsync() ← ffmpeg 支持（ogg/opus/ape/tak/wv/mka）
    └── _player.Source = source
```

**关键文件**：
- `Services/PlaybackService.cs` — 559 行，核心播放服务
- `MainWindow.xaml.cs` — UI 绑定（音量、进度条、播放/暂停）
- `MusicPlayer.csproj` — WinUI 3 / Windows App SDK 1.8，`net10.0-windows10.0.26100.0`

**特点**：
- 每首曲目独立 `MediaSource`（非 `MediaPlaybackList`）→ 手动管理队列
- `MediaPlaybackSession` 驱动位置更新（`PositionChanged` 事件）
- SMTC 集成（系统媒体控制、蓝牙耳机、锁屏）
- 无任何音频效果/均衡器代码

---

## 2. 路径 A：MediaPlayer.AddAudioEffect + EqualizerEffectDefinition

### 结论：❌ 不可行

**原因**：`EqualizerEffectDefinition` 的工厂需要 `AudioGraph` 实例，而 `MediaPlayer.AddAudioEffect` 接受的是 Media Foundation 扩展激活 ID（`String activatableClassId` + `IPropertySet`）。

### API 签名

```csharp
// MediaPlayer.AddAudioEffect（Win10 1511+）
public void MediaPlayer.AddAudioEffect(
    String activatableClassId,      // 激活类 ID（如自定义 WinRT 组件的 FullName）
    Boolean effectOptional,          // 可选效果（失败不中断播放）
    IPropertySet configuration       // 配置参数
);

// EqualizerEffectDefinition（AudioGraph 专用）
public sealed class EqualizerEffectDefinition
{
    public EqualizerEffectDefinition(AudioGraph audioGraph);  // ← 需要 AudioGraph！
    public IVectorView<EqualizerBand> Bands { get; }          // 只读，4 个频段
    public string ActivatableClassId { get; }
    public IPropertySet Properties { get; }
}
```

### 证据来源

1. **IDL 定义**：`IEqualizerEffectDefinitionFactory.Create(AudioGraph*)` — 工厂方法强制要求 AudioGraph  
   [源码](https://github.com/tpn/winsdk-10/blob/9b69fd26ac0c7d0b83d378dba01080e93349c2ed/Include/10.0.14393.0/winrt/windows.media.audio.idl)

2. **Microsoft Q&A**：官方回答确认 "MediaPlayer could not use AudioGraph directly"  
   [来源](https://learn.microsoft.com/en-us/answers/questions/1280085/how-can-i-add-equalizer-effect-to-the-mediaplayer)

3. **Stack Overflow**：多位开发者确认 MediaPlayer 无法直接使用 AudioGraph 效果  
   [来源](https://stackoverflow.com/questions/65613007/uwp-add-equalizer-to-mediaplayer)

**如果坚持用 MediaPlayer**：必须编写自定义 WinRT 组件实现 `IBasicAudioEffect` 接口（手动实现双二次滤波器），工作量大且无必要。

---

## 3. 路径 B：AudioGraph 接管播放管道

### 结论：✅ 可行（推荐路径）

**核心 API**：`AudioGraph.CreateMediaSourceAudioInputNodeAsync(MediaSource)`  
**最低版本**：Windows 10 1803（Build 17134），我们的目标是 19041+ ✓

### AudioGraph 管道设计

```
MediaSource (FFmpegMediaSource / native MediaSource)
    ↓
MediaSourceAudioInputNode (带 EffectDefinitions)
    ├── EqualizerEffectDefinition × 3 (12 段 = 3×4)
    │   ├── 定义 1: 63Hz, 125Hz, 250Hz, 500Hz
    │   ├── 定义 2: 1kHz, 2kHz, 4kHz, 8kHz
    │   └── 定义 3: 10kHz, 12kHz, 14kHz, 16kHz
    ↓
AudioDeviceOutputNode (扬声器输出)
```

### 关键 API 示例

```csharp
// 1. 创建 AudioGraph
var settings = new AudioGraphSettings(Render.AudioRenderCategory.Media);
settings.DesiredRenderDeviceAudioProcessing = AudioProcessing.Default;
var graphResult = await AudioGraph.CreateAsync(settings);
var audioGraph = graphResult.Graph;

// 2. 创建设备输出节点
var outputNodeResult = await audioGraph.CreateDeviceOutputNodeAsync();
var deviceOutputNode = outputNodeResult.DeviceOutputNode;

// 3. 创建 MediaSource 输入节点
var mediaSource = MediaSource.CreateFromStorageFile(file);
var inputNodeResult = await audioGraph.CreateMediaSourceAudioInputNodeAsync(mediaSource);
var mediaSourceInputNode = inputNodeResult.Node;

// 4. 添加均衡器效果（堆叠 3 个定义实现 12 段）
var eq1 = new EqualizerEffectDefinition(audioGraph);
eq1.Bands[0].FrequencyCenter = 63.0;    // 低音
eq1.Bands[1].FrequencyCenter = 125.0;
eq1.Bands[2].FrequencyCenter = 250.0;
eq1.Bands[3].FrequencyCenter = 500.0;

var eq2 = new EqualizerEffectDefinition(audioGraph);
eq2.Bands[0].FrequencyCenter = 1000.0;  // 中音
eq2.Bands[1].FrequencyCenter = 2000.0;
eq2.Bands[2].FrequencyCenter = 4000.0;
eq2.Bands[3].FrequencyCenter = 8000.0;

var eq3 = new EqualizerEffectDefinition(audioGraph);
eq3.Bands[0].FrequencyCenter = 10000.0; // 高音
eq3.Bands[1].FrequencyCenter = 12000.0;
eq3.Bands[2].FrequencyCenter = 14000.0;
eq3.Bands[3].FrequencyCenter = 16000.0;

mediaSourceInputNode.EffectDefinitions.Add(eq1);
mediaSourceInputNode.EffectDefinitions.Add(eq2);
mediaSourceInputNode.EffectDefinitions.Add(eq3);

// 5. 连接管道并播放
mediaSourceInputNode.AddOutgoingConnection(deviceOutputNode);
audioGraph.Start();
```

### EQ 参数范围（来自 FXEQ XAPO）

```cpp
// xapofx.h — 由官方示例引用
#define FXEQ_MIN_FREQUENCY_CENTER  20.0f     // 最低中心频率
#define FXEQ_MAX_FREQUENCY_CENTER  20000.0f  // 最高中心频率
#define FXEQ_MIN_GAIN              0.126f    // -18dB（不能归零！）
#define FXEQ_MAX_GAIN              7.94f     // +18dB
#define FXEQ_DEFAULT_GAIN          1.0f      // 0dB（无变化）
#define FXEQ_MIN_BANDWIDTH         0.1f
#define FXEQ_MAX_BANDWIDTH         2.0f
#define FXEQ_MIN_FRAMERATE         22000     // 最低采样率
#define FXEQ_MAX_FRAMERATE         48000     // 最高采样率
```

**重要**：`Gain` 是**线性振幅**，不是 dB！
- `1.0` = 0dB（平坦）
- `0.126` = -18dB（最小衰减）
- `7.94` = +18dB（最大增益）
- **不能设置 0 gain**（会抛异常）

---

## 4. 音频格式约束

| 约束 | 值 | 影响 |
|------|-----|------|
| 音频格式 | **仅 FLOAT32** | AudioGraph 内部处理默认用 float32，无问题 |
| 采样率 | 22000–48000 Hz | 覆盖所有常见音乐格式（44.1k/48k） |
| 频段数 | 4 / `EqualizerEffectDefinition` | 需堆叠多个定义实现 10+ 段 |
| 增益范围 | 0.126–7.94（-18dB 到 +18dB） | 不能完全静音某频段 |

---

## 5. 延迟特性

| 模式 | 典型延迟 | 说明 |
|------|----------|------|
| AudioGraph Default | ~10ms | 量子缓冲 |
| AudioGraph LowestLatency | 驱动最小值 | 增加功耗 |
| MediaPlayer | ~30-50ms | Media Foundation 管道 |

AudioGraph 的延迟略优于 MediaPlayer，对音乐播放无感知差异。

---

## 6. CPU 占用估算

- **无 EQ**：AudioGraph 播放 ≈ 1-2% CPU（音频解码 + 渲染）
- **10 段 EQ**：额外 < 1% CPU（FXEQ XAPO 非常轻量）
- **总占用**：≈ 2-3% CPU（在现代硬件上可忽略）

**注意**：C# GC 暂停可能导致音频卡顿（[Stack Overflow 报告](https://stackoverflow.com/questions/54598333)），但 AudioGraph 默认缓冲足够吸收短暂停顿。

---

## 7. 已知问题与坑

### ⚠️ 高风险

1. **MediaSourceAudioInputNode 静音问题**
   - **现象**：播放约 7 分钟后输出静音（float buffer 全 0）
   - **来源**：[GitHub Issue #2210](https://github.com/MicrosoftDocs/winrt-api/issues/2210)
   - **状态**：2022 年报告，已在 docs repo 关闭（可能未修复）
   - **缓解**：定期重置节点（每 5 分钟 `Stop()` + `Start()`），或监控 `UnrecoverableErrorOccurred`

2. **同一 MediaSource 不能共享**
   - MediaPlayer 和 AudioGraph 不能同时使用同一个 `MediaSource` 实例
   - **影响**：必须完全迁移到 AudioGraph，不能混合使用

### ⚠️ 中风险

3. **SMTC 集成需要手动迁移**
   - `MediaPlayer` 自带 `SystemMediaTransportControls`
   - AudioGraph 没有内置 SMTC → 需要手动调用 `SystemMediaTransportControlsStatics`
   - 工作量：约 2-3 小时

4. **位置跟踪需要手动实现**
   - `MediaPlayer.PlaybackSession.PositionChanged` 是现成的
   - AudioGraph 需要用 `MediaSourceAudioInputNode` 的属性轮询或使用 `FrameOutputNode` 计数

### ⚠️ 低风险

5. **Gain 不能归零**
   - 最小值 0.126（-18dB），不能完全静音某频段
   - **影响**：用户界面显示需用对数刻度，避免"静音"按钮

6. **GC 暂停卡顿**
   - C# 的 `AudioFrameOutputNode` 可能因 GC 暂停导致音频间隙
   - **缓解**：使用 `unsafe` + 固定缓冲区，或增加缓冲区大小

---

## 8. 预设实现方案

### 推荐频段配置（10 段 EQ）

```csharp
public static class EqPresets
{
    // 频段中心频率（Hz）— 10 段标准 ISO 频段
    public static readonly double[] BandFrequencies = 
    {
        31, 62, 125, 250, 500,  // 低频
        1000, 2000, 4000,       // 中频
        8000, 16000             // 高频
    };
    
    // 预设增益（线性振幅，1.0 = 0dB）
    public static readonly Dictionary<string, double[]> Presets = new()
    {
        ["Flat"]     = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1],        // 平坦
        ["Low"]      = [2.5, 2, 1.5, 1, 1, 1, 1, 1, 1, 1],    // 低音增强
        ["Vocal"]    = [0.5, 0.7, 1, 1.5, 2, 2, 1.5, 1, 0.8, 0.5], // 人声
        ["Rock"]     = [1.8, 1.5, 1, 0.7, 0.8, 1.2, 1.5, 1.8, 2, 1.5], // 摇滚
        ["Electronic"] = [2, 1.8, 1.5, 1, 0.8, 1, 1.2, 1.5, 2, 2.5],   // 电子
    };
}
```

### 频段到 EqualizerEffectDefinition 映射

```csharp
// 10 段 = 3 个 EqualizerEffectDefinition（4+4+2）
var eq1 = new EqualizerEffectDefinition(graph); // 低频 4 段
eq1.Bands[0].FrequencyCenter = 31;
eq1.Bands[1].FrequencyCenter = 62;
eq1.Bands[2].FrequencyCenter = 125;
eq1.Bands[3].FrequencyCenter = 250;

var eq2 = new EqualizerEffectDefinition(graph); // 中频 4 段
eq2.Bands[0].FrequencyCenter = 500;
eq2.Bands[1].FrequencyCenter = 1000;
eq2.Bands[2].FrequencyCenter = 2000;
eq2.Bands[3].FrequencyCenter = 4000;

var eq3 = new EqualizerEffectDefinition(graph); // 高频 2 段
eq3.Bands[0].FrequencyCenter = 8000;
eq3.Bands[1].FrequencyCenter = 16000;
```

---

## 9. 迁移工作量估算

| 步骤 | 工作量 | 说明 |
|------|--------|------|
| 1. 创建 `AudioGraphService` | 4-6 小时 | 替换 `PlaybackService` 的 MediaPlayer |
| 2. 迁移队列管理 | 2-3 小时 | `MediaSourceAudioInputNode` 替换 `MediaSource` |
| 3. SMTC 集成 | 2-3 小时 | 手动调用 `SystemMediaTransportControls` |
| 4. 位置跟踪 | 1-2 小时 | 轮询或 `FrameOutputNode` 计数 |
| 5. EQ UI + 预设 | 3-4 小时 | 滑块 + 预设按钮 + 持久化 |
| 6. 测试 + 修复 | 2-3 小时 | 格式兼容性、静音问题、GC 卡顿 |
| **总计** | **14-21 小时** | ≈ 2-3 个工作日 |

---

## 10. 最终建议

### ✅ GO — 推荐实施

**理由**：
1. AudioGraph 路径完全可行，且有官方示例支持
2. 系统内置 EQ 质量高（XAudio2 FXEQ XAPO），无需第三方库
3. 10 段 EQ + 5 个预设（Flat/Low/Vocal/Rock/Electronic）满足用户需求
4. 迁移工作量可控（2-3 天），不需要重写播放核心

**实施优先级**：
1. **Phase 1**（必须）：AudioGraph 基础管道 + EQ 效果
2. **Phase 2**（必须）：SMTC 集成 + 位置跟踪
3. **Phase 3**（可选）：预设 UI + 持久化

**风险缓解**：
- 针对 MediaSourceAudioInputNode 静音问题，添加定期重置机制
- 针对 GC 卡顿，使用 `unsafe` 固定缓冲区
- 针对 Gain 范围，UI 使用对数刻度（避免显示"静音"）

### ⚠️ 降级方案

如果 AudioGraph 迁移风险过高：
- **降级 1**：使用 `MediaPlayer.AddAudioEffect` + 自定义 `IBasicAudioEffect` WinRT 组件（工作量翻倍，约 4-5 天）
- **降级 2**：放弃系统 EQ，使用 WASAPI 直接回调 + 第三方 DSP 库（最重，不推荐）
- **降级 3**：仅实现预设切换（无实时调整），用 `MediaComposition` 预处理音频（延迟高，不推荐）

---

## 附录 A：参考链接

- [EqualizerEffectDefinition 官方文档](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.equalizereffectdefinition)
- [EqualizerBand 官方文档](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.equalizerband)
- [AudioGraph 官方文档](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/audio-graphs)
- [MediaPlayer.AddAudioEffect 官方文档](https://learn.microsoft.com/en-us/uwp/api/windows.media.playback.mediaplayer.addaudioeffect)
- [FXEQ XAPO 参数](https://github.com/tpn/winsdk-10/blob/9b69fd26ac0c7d0b83d378dba01080e93349c2ed/Include/10.0.14393.0/um/xapofx.h)
- [官方示例：AudioCreation Scenario 5](https://github.com/microsoft/Windows-universal-samples/blob/4eb2fcb499c5bc549e918920cfd2b64396a650d9/Samples/AudioCreation/cs/AudioCreation/Scenario5_InboxEffects.xaml.cs)
- [MediaSourceAudioInputNode 静音问题 #2210](https://github.com/MicrosoftDocs/winrt-api/issues/2210)
- [Stack Overflow：Gain 范围问题](https://stackoverflow.com/questions/63415434/no-negative-gain-on-uwp-equalizereffectdefinition)
- [Stack Overflow：MediaPlayer 不能用 AudioGraph](https://stackoverflow.com/questions/65613007/uwp-add-equalizer-to-mediaplayer)

## 附录 B：示例代码片段

### 切换 EQ 预设

```csharp
public void ApplyPreset(string presetName)
{
    if (!EqPresets.Presets.TryGetValue(presetName, out var gains))
        return;
    
    // gains 长度必须 = 10（10 段 EQ）
    // eq1, eq2, eq3 是类成员变量
    
    // 低频（eq1: 31, 62, 125, 250）
    for (int i = 0; i < 4; i++)
        eq1.Bands[i].Gain = gains[i];
    
    // 中频（eq2: 500, 1000, 2000, 4000）
    for (int i = 0; i < 4; i++)
        eq2.Bands[i].Gain = gains[4 + i];
    
    // 高频（eq3: 8000, 16000）
    for (int i = 0; i < 2; i++)
        eq3.Bands[i].Gain = gains[8 + i];
    
    // 持久化
    SettingsStore.SaveEqPreset(presetName);
}
```

### 实时调整单个频段

```csharp
public void SetBandGain(int bandIndex, double gain)
{
    // gain: 0.126 (-18dB) 到 7.94 (+18dB)
    // UI 建议：滑块范围 -18 到 +18 dB，内部转换为线性振幅
    
    double linearGain = Math.Pow(10, gain / 20.0); // dB 转线性
    linearGain = Math.Clamp(linearGain, 0.126, 7.94);
    
    if (bandIndex < 4)
        eq1.Bands[bandIndex].Gain = linearGain;
    else if (bandIndex < 8)
        eq2.Bands[bandIndex - 4].Gain = linearGain;
    else if (bandIndex < 10)
        eq3.Bands[bandIndex - 8].Gain = linearGain;
}
```
