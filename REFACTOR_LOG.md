# REFACTOR_LOG - 项目迭代与重构全局记录

## [2026-10-09] 项目初始化与需求规格设计

### 变更背景
- 用户需求：在开源桌面启动器 Rubick 中二次开发一款专属插件。
- 核心功能：
  1. 类似 Snipaste / 微信截图的全屏遮罩截屏与完整标注工具栏（矩形、折线箭头、画笔、马克笔、马赛克、文字、撤销/重做、贴图/钉图、保存、复制）。
  2. 多模态大模型智能 OCR（支持 Base URL、API Key、Model Name 等多模态模型完全可配置）。
  3. 双栏结果面板：左侧原文可编辑校对修改，右侧流式智能翻译（支持中英日韩等目标语言切换，支持自由配置翻译模型）。

### 本次动作
- 初始化 Git 仓库与 `.gitignore`（隔离 `node_modules` 与 `docs/` 文档）。
- 梳理架构与技术栈：原生 JS + `js-web-screen-shot` + OpenAI 兼容 API 适配层。
- 确立实验室目录架构：`utools_lab` 统一作为插件合集仓，为本插件创建专属独立目录 `screenshotOcrTranslate`，便于后续扩展孵化更多插件。
- 彻底摒弃对本机外部安装软件的依赖，使用 C# 原生编写轻量跨屏选区截屏器源码 `src/snipper/Program.cs`，并使用 Windows 原生编译器编译为 `bin/snipper.exe`（自包含、仅 16KB、零外部依赖）。
- 彻底根治内存堆积与系统假死现象：使用“四矩形几何遮罩法”剔除全景位图的重复缓存，马赛克改为仅针对局部区域按需生成；在程序退出时显式执行 `Dispose()` 与垃圾回收 `GC.Collect()`。
- 解决 API 配置丢失问题：针对 Rubick 无痕/临时 session 下 `localStorage` 会被清空的特性，在 `preload.js` 引入系统用户主目录持久化文件（`~/.rubick_ocr_translate_config.json`），实现保存一次永久保留。
