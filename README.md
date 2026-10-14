# 🚀 SnipAI - 下一代 AI 增强型截图标注、多模态 OCR 翻译与智能改图工作台

<div align="center">

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey.svg)](https://github.com/L3376679936/SnipAI)
[![Version](https://img.shields.io/badge/version-v1.4.0-brightgreen.svg)](https://github.com/L3376679936/SnipAI)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-orange.svg)](https://github.com/L3376679936/SnipAI/pulls)

**集「Snipaste 级像素取色与多功能截图标注」+「多模态大模型 OCR 流式翻译」+「业内首创：分级双引擎无痕改字与局部 AI 重新设计」于一体的终极生产力神器。**

</div>

---

## 🌟 为什么选择 SnipAI？

传统的截屏软件（如 Snipaste、ShareX）止步于“截图和简单画红框”，而当你想**修改图片上的文字**、**把某个图标重新设计**或**翻译外语文档**时，往往需要在 Photoshop、翻译软件和大模型之间反复来回折腾。

**SnipAI 将这一切收拢为一键直达的极速闭环：**

| 功能维度 | 传统截图软件 (Snipaste 等) | 普通网页/微信截图 | 🌟 SnipAI |
| :--- | :--- | :--- | :--- |
| **截图与像素放大镜** | ✅ 具备 | ❌ 无取色 HUD | ✅ **原生像素级放大镜 + 取色 HUD**（按 C 复制 HEX/RGB，按 Shift 切格式） |
| **标注工具链** | ✅ 具备 | ⚠️ 简陋 | ✅ **矩形/椭圆、折线/箭头、画笔、记号笔、马赛克/模糊、序号、文字标注、多步撤销重做** |
| **桌面置顶贴图** | ✅ 具备 | ❌ 不支持 | ✅ **F3 置顶贴图，支持鼠标滚轮缩放、Ctrl+滚轮透明度调节与拖拽移动** |
| **OCR 文字提取与翻译** | ⚠️ 需额外配置或仅识别 | ⚠️ 简单机翻 | ✅ **多模态 Vision 大模型流式输出 + 毫秒级打字机对照翻译 + 历史持久化** |
| **✏️ 图片文字原位无痕修改** | ❌ 无法修改 | ❌ 无法修改 | ✅ **独家「⚡轻量级」：拖拽框选原字，毫秒级智能擦除并原位排版新字（同行后文自动平移消留白，零 Token）** |
| **🎨 选区局部重新设计 (重绘)** | ❌ 无法修改 | ❌ 无法修改 | ✅ **独家「🎨重绘级」：框选任意区域输入自然语言（如“把充电桩改成精致3D立体风格”），AI 自动理解底色无缝镶嵌** |

---

## ✨ 核心特性一览

### 1. 🔍 Snipaste 级极客截屏与像素取色 HUD
* **自包含独立运行引擎**：基于 C# 原生编译，不依赖任何第三方重量级环境，毫秒级极速唤起；
* **像素放大镜 & 颜色拾取**：在屏幕上游标移动时实时呈现像素网格、坐标与色彩值；
  * 按 **`C`**：一键复制当前像素颜色值到剪贴板；
  * 按 **`Shift`**：在 `HEX (#FFFFFF)` 与 `RGB (255, 255, 255)` 格式间快速切换；
* **完整标注生态**：几何图形（矩形/椭圆）、引导线条（折线/箭头）、涂鸦画笔、半透明荧光高亮、马赛克/高斯模糊柔化、序号气泡与自由文字。

### 2. 🤖 多模态大模型 OCR 与流式智能翻译
* **全平台模型兼容**：支持 OpenAI 格式兼容的任何 Vision 大模型（Qwen-VL-Max / Qwen-Image、GPT-4o、DeepSeek 系列、Claude 3.5 Sonnet、百度 OCR 等）；
* **流式打字机响应**：双语实时对照，提取结果可直接二次编辑与校对；
* **历史记录持久化**：本地安全加密存储近 30 条识别与翻译记录，支持一键清空与独立管理。

### 3. 🎨 业内首创：分级双引擎 AI 智能改图
* ⚡ **轻量级 · 无痕改字与细节微调 (Zero-Token & 所见即所得)**：
  * 在图上按住鼠标左键圈出文字，算法毫秒级提取背景色并计算汉字字形包络；
  * 输入新文字，画布实时原地无痕渲染，支持多图层增量修改；
  * **智能后文平移回流 (Reflow)**：长字改短字时自动将后半句平移紧贴，杜绝行内大面积留白；
* 🎨 **重绘级 · 局部重新设计 (Inpainting & Resynthesis)**：
  * 框选任意局部区域（如架构图上的某个图标、界面的某个组件）；
  * 输入自然语言指令（例如：*“把框选区域的充电桩重新设计成精致的3D立体渲染风格，光影自然融合”*）；
  * 自动采样选区环境背景色（如深黑科技底色与发光底座），AI 生成后自动执行智能抠底与边缘羽化，将新设计的 3D 图案无缝镶嵌进原图。

---

## 🌿 双分支架构（兼备独立桌面软件与 uTools 插件生态）

为了兼顾“独立运行的桌面极客工具”与“官方应用市场零二进制审核红线”，本项目采用双分支架构：

| 分支名 | 定位 | 特点 |
| :--- | :--- | :--- |
| **`master` / `main`** *(推荐)* | **独立全功能版 (Standalone)** | 内置 C# 原生高效截图引擎与像素取色 HUD，无需 uTools 也能在 Windows 下作为一个完全独立的 Snipaste 级截图标注与改图神器使用。 |
| **`release/utools-pure-js`** | **uTools 市场过审合规版** | 彻底剔除 `.exe` 二进制文件，全面接入 `utools.screenCapture` 官方 API，体积仅 100KB，专供提交给 uTools 应用市场审核上架。 |

---

## 🛠️ 快速开始

### 方式一：在 uTools / Rubick 中直接加载开发
1. 克隆本项目：
   ```bash
   git clone https://github.com/L3376679936/SnipAI.git
   cd SnipAI
   ```
2. 打开 **uTools 开发者工具**；
3. 点击「新建项目」，选择本项目中的 `screenshotOcrTranslate` 文件夹；
4. 点击「开启运行」即可直接呼出体验！

### 方式二：独立编译 C# 截屏引擎 (`master` 分支)
本项目截屏引擎采用零第三方依赖的 .NET 原生实现，可直接用系统内置编译器一键编译：
```bash
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -target:winexe -optimize+ -nologo -r:System.Drawing.dll,System.Windows.Forms.dll,System.dll -out:screenshotOcrTranslate\bin\snipper.exe screenshotOcrTranslate\src\snipper\Program.cs
```

---

## 🤝 贡献与反馈
欢迎提交 Issue 交流想法或发起 Pull Request！如果你觉得这个工具好用，请给项目点个 ⭐️ **Star** 鼓励一下作者！

---

## 📄 开源许可证
本项目采用 [MIT License](LICENSE) 开源许可证。
