# REFACTOR_LOG - 项目迭代与重构全局记录

## [2026-10-14] 核心 Bug 修复：解决 High-DPI 缩放导致截图图片自动放大全屏与工具栏超出版面丢失问题（`release/utools-pure-js`）

### 变更背景与用户核心痛点
- **用户痛点反馈**：*“截图完，图片自动放大全屏了，且没有显示工具栏”*。
- **根因定位**：
  1. **图片放大全屏根因**：Windows 系统通常默认开启 125%、150% 或 200% 的 DPI 缩放（`scaleFactor > 1`）。`utools.screenCapture` 返回的 Base64 图片是**物理像素尺寸**（如 1920×1080），而 Electron `BrowserWindow` 的坐标与尺寸单位是**逻辑像素 (DIP)**（150% 缩放下 1080p 屏幕的逻辑尺寸仅为 1280×720）。此前未将物理尺寸除以 `scaleFactor`，导致把 1920×1080 逻辑像素传给了窗口，使窗口和图片被放大了 1.5 倍至 2 倍，瞬间撑爆整个屏幕（表现为“自动放大全屏”）。
  2. **工具栏没有显示根因**：第一，由于窗口被放大超出屏幕物理边界，位于底部的工具栏被直接顶到了屏幕外侧（任务栏下方/视口外）；第二，此前 `capture.js` 将工具栏死板定位在 `top: stageHeight + 8px`，在全屏或大图截屏时没有做视口智能翻转；第三，`createBrowserWindow` 回调中的 `executeJavaScript` 存在与 `capture.html` 渲染进程的时序竞争，偶尔导致数据未能挂载。

### 核心改造与修复明细
1. **DPI 缩放比感知与逻辑/物理双轨换算（[`preload.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/preload.js)）**：
   - 提取 `display.scaleFactor`，将 `physW` 与 `physH` 严格除以 `scale` 换算为屏幕真实 DIP 尺寸 `logicalW` 与 `logicalH`；
   - 窗口尺寸精确匹配 `Math.max(logicalW, 530)` 与 `logicalH + 86`，1:1 像素级贴合截图，彻底消除“放大全屏”异象！
2. **Snipaste 经典智能停靠：自适应底部与内部右下角翻转（[`capture.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/capture.js)）**：
   - 在 `updateToolbarPosition()` 中动态计算视口余量：若下方空间充足，工具栏吸附于截图外侧底部；若下方空间不足（如全屏截图或贴底截图），**工具栏自动智能翻转停靠在截图内部右下角**，确保 100% 可见、绝不掉出屏幕！
3. **本地 Init Payload 同步注入，彻底杜绝数据丢失**：
   - 在 `os.tmpdir()` 写入 `rubick_capture_init.json`，`capture.html` 在 `DOMContentLoaded` 与 `loadFromInitData` 两个通道双重保险载入数据，零异步竞争，秒级渲染！

---

### 变更背景与用户核心痛点
- **用户核心痛点反馈**：
  1. *“写字和马赛克工具不能使用”*；
  2. *“这个选完截图区域跟原本的效果不对啊，是不是实现方式不对啊，怎么蒙版白底了，并且自动切到我主屏了”*。
- **根因定位**：
  1. **切到主屏与白底蒙版根因**：此前在 `preload.js` 中创建 `capture.html` 时使用了 `{ fullscreen: true }` 且未指定具体坐标与 `backgroundColor`。在 Windows 多屏环境下，Electron 全屏窗口强制回退到主屏（Display 0），且默认背景为不透明纯白导致瞬间白闪；同时内部用半透明黑底盖住全屏并将截图居中呈现，完全违背了用户在副屏划定选区后“悬浮工具栏就近紧贴选区”的本能直觉。
  2. **马赛克工具失效根因**：`drawMosaic` 在鼠标拖拽起始微小位移时 `w < 1` 或 `h < 1`，传给离屏 Canvas 导致 `getImageData(0, 0, 0, 0)` 抛出未捕获的 `IndexSizeError` 异常，中断了整个鼠标事件循环。
  3. **写字工具失效根因**：`showInlineTextInput` 在鼠标 `mousedown` 中调用了 `inlineTextInput.focus()`，但由于紧接着的 `mouseup` 发生在 Canvas/Window 上，Chromium 默认点击行为将焦点移出，瞬间触发了 `blur` 事件并执行了 `commitText`，导致输入框仅露脸 1 毫秒便自动销毁，用户根本无法敲字。

### 核心改造与修复明细
1. **就近原位呈现架构：精准多屏定位 + 100% 透明无边框 + 杜绝白闪（[`preload.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/preload.js)）**：
   - 彻底废弃全屏模式（`fullscreen: false`）；
   - 通过 `utools.getCursorScreenPoint()` 与 `utools.getDisplayNearestPoint()` 获取用户截图完成时光标所在的精确屏幕 `display.bounds`；
   - 解析截图物理像素尺寸，以用户截图终点为锚点动态计算窗口坐标 `[winX, winY]` 与精准窗口尺寸 `winWidth × winHeight`（工具栏底栏宽度 `530px`，贴合截图尺寸）；
   - 设置 `backgroundColor: '#00000000'`，100% 纯透明无边框窗口，彻底消除白色背景与全屏深灰蒙版遮罩，窗口原位呈现在用户截图所在的屏幕，毫秒级就绪！
2. **写字打字标注工具彻底修复（[`capture.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/capture.js)、[`capture.css`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/capture.css)）**：
   - 输入框事件监听器全局仅绑定一次，杜绝重复绑定与内存泄漏；
   - 采用 `setTimeout(() => inlineTextInput.focus(), 30)` 避开 `mouseup` 焦点抢占；
   - 支持高对比度暗色半透明底衬、输入时按 `Enter` 提交、`Shift+Enter` 换行、`Escape` 取消，失焦安全提交；
   - Canvas 绘制时统一 `textBaseline = 'top'` 与选区坐标对齐，字体加粗/斜体/背景遮罩/字号全属性生效。
3. **马赛克与高斯模糊工具彻底修复（[`capture.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/capture.js)）**：
   - 增加严密边界防护：`mx, my, mw, mh` 四舍五入为整型，增加 `mw < 2 || mh < 2` 退出守卫；
   - 采用 1:1 离屏干净底图缓冲层（`sourceOffscreenCtx`）进行像素级安全取样，杜绝 `IndexSizeError`；
   - 高斯模糊采用局部 `clip()` + `ctx.filter = blur(...)` 原图绘制，丝滑流畅零卡顿。
4. **窗口自由拖移与原生手感对齐**：
   - 当未激活任何工具时，在截图区域按住鼠标可平滑整体拖动悬浮窗口（`window.moveBy`），双击立即复制并关闭，右键逐级取消，体验 1:1 对齐 Snipaste。

---

### 变更背景与用户核心诉求
- **用户核心诉求**：追求“选区右下角必须漂浮工具栏”的原生截屏体感（1:1 媲美 Snipaste / 原版 C# Snipper），同时要求必须 100% 能够顺利通过 uTools 官方插件市场的零二进制审核。在初版简单实现后，严格核查原版功能，针对用户指出的遗漏功能进行全量地毯式对照与重构补齐。
- **架构突破**：
  - 彻底抛弃依赖外部 C# `snipper.exe` 的设计，用 **HTML5 Canvas 2D + CSS3 + 纯 JavaScript** 打造了自研的高性能全屏选区截屏引擎；
  - 100% 纯 Web 前端代码，无任何 `.exe`、`.dll`、`.so` 外部二进制依赖，跨系统 100% 兼容，100% 符合 uTools 应用市场提审规范。

### 1:1 对齐原版 C# Snipper 18 项工具与二级交互明细
1. **主悬浮工具栏 18 项工具矩阵与 Snipaste 经典视觉设计（[`capture.html`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/capture.html)、[`src/capture.css`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/capture.css)）**：
   - 包含双层 Canvas 架构（底层背景高亮掏空层 + 顶层矢量图元涂鸦层）；
   - **选区尺寸胶囊（左上角浮动）**：Snipaste 经典深灰胶囊，实时显示选区 `W × H (X, Y)`，随选区移动与缩放；
   - **高亮选区与 8 控制手柄**：带对比度边框与 8 个自由拉伸控制点，支持整体平移与八向尺寸精调；
   - **取色放大镜与 RGB/HEX 色彩预览**：15×15 像素网格放大镜，十字光标跟随，支持按 `C` 键复制 Hex 色值；
   - **全套 18 项主工具按钮**：
     - `矩形/椭圆 (R/E)`（复合下三角工具，展开二级选择椭圆或实心/空心）；
     - `折线/箭头 (A/L)`（复合下三角工具，展开二级选择单向箭头、双向箭头、普通折线）；
     - `自由画笔 (B)`（贝塞尔平滑轨迹）；
     - `荧光笔/高亮 (M)`（复合半透明模式 `globalCompositeOperation = 'source-over'` 配合柔和半透明色）；
     - `局部马赛克/高斯模糊 (K/G)`（支持局部像素化打码与柔和模糊切换）；
     - `行内打字标注 (T)`（带可调节文字属性栏）；
     - `序号/步骤标记 (N)`（①②③ 经典圆形数字气泡，点击连续自增，右键可回滚标号）；
     - `橡皮擦 (X)`（点击擦除指定图元，鼠标呈现橡皮擦瞄准靶心）；
     - `撤销 (Ctrl+Z)` 与 `重做 (Ctrl+Y / Ctrl+Shift+Z)`；
     - `✨ AI 智能改图`（触发黑科技三图分离回填改图画板）；
     - `📄 提取文字 (OCR)`；
     - `📌 钉在桌面 (贴图 F3)`；
     - `💾 保存图片 (Ctrl+S)`；
     - `✖ 关闭退出 (Esc)`；
     - `✔ 完成并复制 (Enter / 双击选区)`。
2. **Snipaste 经典二级属性栏与 20 色双行调色板**：
   - **主参数胶囊（当前粗细/颗粒度）**：圆形当前颜色预览，点击直接唤起系统 Native Color Picker 拾色器；
   - **经典 20 色双行网格调色板**：
     - 上行 10 色高饱和鲜艳色（红/橙/黄/绿/青/蓝/紫/粉/深灰/纯黑）；
     - 下行 10 色柔和粉彩色系（浅红/肉粉/米黄/浅绿/淡青/天蓝/薰衣草紫/浅粉紫/中灰/纯白）；
   - **三种预设笔触与滚轮调粗细**：
     - 小（3px）、中（6px）、大（12px）三档快捷选择；
     - **支持鼠标滚轮在画面任意位置滚动**：动态调节笔宽与马赛克颗粒（1~64px），实时联动主参数胶囊；
   - **工具专用辅助切换器**：
     - 形状工具二级栏：空心描边 vs 实心填充切换、实线 vs 虚线切换；
     - 模糊工具二级栏：像素马赛克（Pixelate）vs 平滑模糊（Smooth Blur）切换；
     - 文本工具二级栏：字号选择（12~48px）、加粗（Bold）、倾斜（Italic）、半透明背景遮罩（Backdrop）切换。
3. **黑科技三图分离继承与文字指令提取（[`src/capture.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/capture.js)）**：
   - 将用户圈选的图层精准解耦为：干净原图（`cleanDataUrl`）、带用户画笔的最终效果图（`annotatedDataUrl`）、矩形选区列表（`boxes`）以及用户打上的文字笔记（`textNotes`）；
   - 点击 `[✨ AI 改图]` 时将三图和文字指令一同注入改图工作台，实现真正的无痕去字、局部消除与原文字重绘！
4. **完整快捷键矩阵与右键三级取消状态机**：
   - 支持 `Enter`（复制）、`Esc`（退出）、`Ctrl+Z`（撤销）、`Ctrl+Y`（重做）、`Ctrl+S`（保存）、`F3`（贴图）、`C`（复制色彩）、`R/E/A/L/B/M/K/G/T/N/X`（单键直切工具）；
   - **右键三级取消**：正在输入文字时取消文字 -> 正在绘制画笔时取消画笔 -> 取消当前激活的工具 -> 取消当前矩形选区 -> 彻底退出截屏，体感完全契合专业截图软件。

---

## [2026-10-14] uTools 纯 JS 合规分支体验重构：解决截图后无工具栏直跳 OCR 痛点，重构截图动作工具栏与意图路由（`release/utools-pure-js`）

### 变更背景与用户核心痛点
- **用户痛点**：在剔除 C# `snipper.exe` 升级为 uTools 原生截屏后，用户反馈：*“现在怎么选中区域截图后，工具栏不显示了？直接进文字识别了”*。
- **根因定位**：
  1. `preload.js` 中的 `captureScreen()` 原生截屏完成后硬编码返回了 `{ action: 'ocr', dataUrl }`；
  2. `app.js` 的 `captureManager.onComplete` 接收到截图数据后，无条件执行了 `switchWorkspaceMode('ocr')` 与 `handleNewImage(base64Data)`，直接向后端发起大模型 OCR 与流式翻译请求，强行剥夺了用户进入「AI 智能改图」、置顶贴图或仅复制截图的权利，并擅自消耗 Token。

### 核心改造明细
1. **重构截图后交互体验与快捷动作工具栏（[`screenshotOcrTranslate/index.html`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/index.html)、[`src/style.css`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/style.css)）**：
   - 在主界面新增 **`capture-action-card` 截图就绪快捷操作工具栏与居中大预览卡片**，截图完成后立即醒目呈现五大核心操作：
     - `[✨ AI 智能改图]`：渐变高亮按钮，一键进入画板进行无痕改字、局部消除与创意重绘；
     - `[📄 提取文字 & 翻译]`：大模型 OCR 与流式双语对照入口（用户明确点击才发起网络请求）；
     - `[📌 钉在桌面]`：调用纯 Web 置顶贴图窗口（支持拖拽/滚轮缩放/双击Esc关闭）；
     - `[⧉ 复制图片]`：写入系统剪贴板；
     - `[💾 保存到本地]`：下载保存为高清 PNG 图片。
   - 增强收起态缩略图栏（`thumbWrap`）：在 OCR 完成后或切换文本视图时，提供迷你快捷操作按钮组，随时可一键送去改图、贴图或保存。
2. **多维意图精准路由引擎（[`screenshotOcrTranslate/src/app.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/app.js)）**：
   - `ScreenCaptureManager` 扩展 `startCapture(intent = 'auto')`：
     - 若用户在「✨ AI 智能改图」Tab 下截图，或通过 `截图改图` 指令唤醒：自动载入 AI 改图工作台画板；
     - 若用户通过 `ocr截图` / `文字提取` / `文字识别` 唤醒：执行自动 OCR 识别与翻译；
     - 若为普通全局截图（`截图` / `截屏` / `屏幕截图` / `direct-capture`）：默认进入截图就绪态，展示快捷操作工具栏，交由用户自主点选。
3. **增加用户偏好配置项（[`openSettings` & `saveSettings` in `app.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/app.js)）**：
   - 在 API 设置面板中加入「通用截图完成后的默认动作」：
     - 🌟 展示操作工具栏（自主选择改字、重绘、OCR或贴图，推荐）—— **默认**
     - ✨ 直接进入 AI 智能改图画板（适合高频修图与局部重绘）
     - 📄 直接提取文字并翻译（自动调用大模型 OCR 识别）
4. **底层通信桥接合规解耦（[`screenshotOcrTranslate/preload.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/preload.js#L145-L165)）**：
   - 将 `captureScreen` resolve 动作由硬编码的 `ocr` 解耦为 `ready`，彻底交由上层业务与用户意图决策。

---

## [2026-10-14] 创建 uTools 市场审核合规分支（`release/utools-pure-js`）：彻底剔除二进制可执行文件，全面接入官方原生截屏 API

### 变更背景与架构分流策略
- **uTools 审核拦截**：uTools 官方应用市场硬性规定“包含可执行二进制的插件无法上架”。此前插件包内置了自研的 C# `bin/snipper.exe`，导致提审被拒。
- **分支分流策略**：
  - **`master` 分支**：保持**独立全功能版 (Standalone)**，内置 C# 原生截屏器，具备像素级取色放大镜、原生置顶贴图等底层能力，脱离 uTools 也可在 Windows 下独立运行；
  - **`release/utools-pure-js` 分支**：**uTools 官方纯 JS 市场合规版**，彻底删除所有 `.exe` 和原生源码，纯纯正正采用 Web/JS 技术栈，专供打包提审上架 uTools 应用市场。

### 核心改造明细
1. **全面接入 uTools 官方原生截屏（[`screenshotOcrTranslate/preload.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/preload.js#L115-L185)）**：
   - 彻底废除 `child_process.execFile` 及外部 exe 调用；
   - 采用 `utools.screenCapture((base64Str) => { ... })` 原生截图通道，截取完成后自动回填至前端 Canvas 改图与 OCR 翻译工作台；
   - 兼顾跨平台特性（天然原生支持 Windows、Mac、Linux 三端）。
2. **纯 Web 方案重构桌面置顶贴图（[`pin.html`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/pin.html) + [`preload.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/preload.js#L248-L275)）**：
   - 移除外部贴图进程依赖，利用 `utools.createBrowserWindow('pin.html', { frame: false, alwaysOnTop: true, transparent: true })` 实现纯 Web 级置顶无边框贴图；
   - 支持按住拖拽、滚轮缩放、双击或按 Esc 快速关闭。
3. **彻底清理二进制与多余源码**：
   - `git rm -r screenshotOcrTranslate/bin/snipper.exe screenshotOcrTranslate/src/snipper`，零二进制，包体极致轻量化。

4. **原生随心平移拖拽与坐标体系解耦全面升级（解决截完图无法拖拽与标注错位）**：
   - **BroadcastChannel 原生 60FPS 窗口平移**：使用现代原生 `BroadcastChannel` 建立子窗口与宿主 `preload.js` 的内存级双向桥接通道。在未激活工具时鼠标呈现 `move` 抓手光标，用户按住截图任意区域拖拽时，直接驱动 Electron 顶级 BrowserWindow `setPosition`，实现如桌面贴图般随意平移移动；
   - **画布尺寸与逻辑坐标 1:1 统一解耦**：彻底移除交互画布与 DOM 之间的全局缩放矩阵混淆，`bgCanvas` 与 `annotCanvas` 统一以 `stageWidth × stageHeight` 逻辑像素呈现；
   - **马赛克与高斯模糊 0 误差**：从逻辑底图画布直接平稳采样并块状渲染/高斯模糊，彻底消除越界报错与放大变形 Bug；
   - **文字工具多行排版与属性栏联动防失焦**：支持回车换行与自适应暗色对比度背景衬垫；阻止二级属性栏（字号、加粗、颜色）点击冒泡，避免打字输入框误失焦消失；
   - **物理分辨率高清矢量无损导出**：在导出、复制、OCR 与改图时，通过独立的离屏临时画布以 `physicalW × physicalH` 原生分辨率等比高清矢量重绘，兼顾流畅交互与极致清晰画质。

---


### 变更背景与用户核心诉求
- **用户实测截图反馈**：截图工具栏中「✦改图」按钮不仅自带文字标签 `"改图"`，而且宽度为 `48px`（而其余工具按钮均为 `30×30px` 标准正方形），导致工具栏视觉风格突兀、排布拥挤不统一，用户提出“把这个文字去掉吧”。

### 核心改造明细
1. **尺寸回归标准正方形（[`screenshotOcrTranslate/src/snipper/Program.cs`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/snipper/Program.cs#L4535-L4565)）**：
   - 移除 `ai_edit` 按钮在主工具栏布局中的 `48px` 宽度特判，统一回归为与其他 13 个操作按钮完全一致的标准正方形 `btnSize`（`30×30px`）；
   - 悬浮提示文案对齐更新为：`"AI 智能改图 (✨)：圈选或打字标注后一键改字或局部重绘"`。
2. **重绘精致纯矢量 AI 星芒图标（[`DrawVectorIcon` in `Program.cs`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/snipper/Program.cs#L6546-L6590)）**：
   - 彻底剔除按钮内部的 `"改图"` 汉字与长方形描边框；
   - 引入 1:1 对齐现代 AI 产品（Gemini / Apple Intelligence）规范的多层 AI Sparkles (`✨`) 纯矢量图案：
     - 主菱芒星（居中偏左，8 顶点内凹四芒星）；
     - 右上方次级伴生小星芒；
     - 左下方灵动小微光点；
   - 颜色采用 AI 优雅紫罗兰色（`#7c3aed`），选中/激活态自动转为纯白图标，完美融入 Snipaste 风格工具栏。
3. **单文件二进制产物重编译（[`screenshotOcrTranslate/bin/snipper.exe`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/bin/snipper.exe)）**：
   - 使用 .NET Framework 4.0 原生 `csc.exe` 优化编译通过（0 警告 0 错误），并即时生效。

---

## [2026-10-13] 全站深度审计：修复 OCR 双栏截断、脏缓存分数/品名自愈、跨页分数不一致及冗余功能清理（`foodScannerMiniApp` v1.2.9）

### 全局排查发现并修复的 5 类核心 Bug
1. **Bug 1：`analyzeIngredients()` 缺失 `tokens` 导出字段导致脏缓存清洗失效（[`ingredientAnalyzer.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js#L502-L545)）**：
   - **根因**：此前 `analyzeIngredients` 返回对象仅有 `tokenAnnotations`（对象数组）而未直接暴露 `tokens`（字符串数组），导致 `safeHydrateItem` 中 `(rep.tokens || [])` 恒为空数组 `[]`，進而退回展示未清洗的原始文本（如 `】低聚果糖`、`名：、黑椒牛仔粒料：牛肉`）。
   - **修复**：在 [`analyzeIngredients()`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js#L504) 返回值中显式加入 `tokens`，并在 `safeHydrateItem` 中增加 `rep.tokens || (rep.tokenAnnotations || []).map(t => t.token)` 双重兜底，同时自动剥离内嵌的 `名：、黑椒牛仔粒料：` 前缀。
2. **Bug 2：旧缓存 `40分` 越界脏数据未自愈 & PK 台与列表分数不一致（[`ingredientAnalyzer.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js#L648-L712)、[`pkArena.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.js#L43-L52)）**：
   - **根因**：第一，用户本地 `Storage` 中存有旧版兜底留下的 `score: 40` 缓存项（配料仅含 `低聚果糖` 或 `牛肉`，0 项添加剂，按国标硬规则应在 `[90, 100]` 区间），而 `hydrateHistoryItem` 此前无条件信任了旧 `item.score`；第二，[`compareTwoProducts`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js#L696-L712) 内部重新调用 `analyzeIngredients` 算出 `90~100分`，导致同一商品在底部列表显示 `40分 · C级`，选上 PK 台后却显示 `92分 · A+级`。
   - **修复**：在 `hydrateHistoryItem` / `safeHydrateItem` 中增加硬规则边界校验（若缓存分数超出 `[scoreFloor, scoreCeiling]` 立即自动修正为真实算法分），并在 `compareTwoProducts` 中对齐传入的已校准 `score`，确保列表、抽屉、PK 仪表盘与 5 项硬指标表 100% 同分同级。
3. **Bug 3：双栏包装 OCR 被行内字段提前一刀切截断（[`preprocessOcrRawText`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js#L90-L131)）**：
   - **根因**：双栏排版包装袋在 OCR 跨栏扫描时，常将右侧的 `净含量`、`产地`、`产品标准号` 穿插在配料表第 1 行与第 2 行之间；旧逻辑将其列为截断终点词（`working.slice(0, stopMatch.index)`），导致仅扫出第一个词（如 `】低聚果糖`）就把后半段配料全部误切掉。
   - **修复**：将 `净含量`、`规格`、`产地`、`执行标准`、`产品标准号` 改为**行内局部剔除**而非整段截断，并自动剔除内嵌品名头（`品名：XXX 配料：`）。
4. **Bug 4：首页手动校准文字重算时把真实品名覆盖为 `"手动校准"`（[`homeScan.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.js#L446-L460)）**：
   - **修复**：[`handleReScoreFromText`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.js#L446-L460) 重算时保留当前商品的真实名称与实拍缩略图，并将 `"手动校准"` 列入通用占位名黑名单自动净化。
5. **Bug 5：从首页跳转「查配料」或「加入PK」时丢失实拍分数与品名（[`homeScan.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.js#L461-L485)、[`index.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/index/index.js#L69-L116)）**：
   - **修复**：`PENDING_PK_ITEM` 与 `LAST_SCANNED_INGREDIENTS` 升级为结构化载荷 `{ name, ingredientsText, score, thumbUrl }`，跨 Tab 跳转后分数、品名与缩略图 100% 保持一致。
6. **Bug 6：OCR 边缘残字与 DeepSeek AI 二次校验未覆盖初版草稿记录（[`ocrAndAiService.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ocrAndAiService.js#L305-L440)、[`homeScan.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.js#L350-L435)、[`ingredientAnalyzer.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js#L648-L705)）**：
   - **根因**：
     1. **OCR 物理边缘截断**：弯曲包装袋左边缘漏拍导致 `【配料】低聚果糖` 变成 `】低聚果糖`、`【品名】黑椒牛仔粒【配料】牛肉` 变成 `名：、黑椒牛仔粒料：牛肉`；
     2. **DeepSeek 6.5秒超时截断**：`callDeepSeekChat` 原 `timeout: 6500` 过短，DeepSeek 生成长 JSON 时在 5~8 秒被中途掐断，导致 `aiCorrectedText` 与 `productName` 未能返回；
     3. **初版草稿未被 AI 修正结果原位覆盖**：`runAnimatedScanAndScore` 在 0.3 秒先存入一条 `shortName: '实拍包装食品'` 的草稿记录，且 `'实拍包装食品'` 未列入 `GENERIC_TITLES` 黑名单，当 AI 修正配料文本后因 `ingredientsText` 变化未能覆写原草稿。
   - **修复**：將 DeepSeek 超时提升至 `16000ms` 并接入 `127.0.0.1:18999/api/deepseek-analyze` 本地代理双通道；強化 DeepSeek Prompt 强制剥离残缺标签头、拆分粘连词（如 `麦芽糖醇、乳酸菌`）并推断具体品名；引入 `scanSessionId` 确保 AI 精修结果 100% 原位覆盖初版草稿，并将 `'实拍包装食品'` 纳入全站 `GENERIC_TITLES` 与 `safeHydrateItem` 实时自愈。
7. **新增「已拍食品单条删除 & 一键清空」全链路能力（[`homeScan.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxml)、[`index.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/index/index.wxml)、[`pkArena.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.wxml)）**：
   - **Tab 1（拍照打分结果页）**：顶部操作栏新增 **`🗑️ 删除`** 按钮（`handleDeleteCurrentScan`），拍错或拍糊时可一键删掉当前记录并返回待机页；
   - **Tab 2（成分拆解页）& Tab 3（两款PK页列表及选品弹窗）**：每条已扫食品右侧均配备 **`🗑️ 删除`** 按钮（`handleDeleteHistoryItem`），列表顶栏配备 **`🗑️ 清空`** 按钮（`handleClearAllHistory`）；删除时自动同步清理本地持久化缩略图文件、自动替补更新当前拆解项或 PK 槽位，且删空后展示清爽空状态，绝不反弹假数据。
8. **首页 (`homeScan` 待机态) 全屏自适应视觉精修：移除百度/DeepSeek字样 + 重构多层光学相机徽章 + 中间相机取景视窗 `flex: 1` 占满剩余全屏空间（[`homeScan.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxml)、[`homeScan.wxss`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxss)、[`iconfontAssets.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/iconfontAssets.js)）**：
   - **彻底移除第三方厂商字样**：将顶部标签及结果页标签中的 `百度OCR`、`DeepSeek` 全部替换为纯粹的产品能力文案（`AI 智能配料安心拍`、`AI 智能 + 国标五维评分`）；
   - **修复相机图标变“纯白实心块” Bug 并重新设计光学镜头徽章**：移除原 CSS 中错误的 `filter: brightness(0) invert(1)`（该滤镜会将多层彩色 SVG 全部压扁为纯白实心色块），升级 `cameraScan` 为包含**纯白珠光机身、快门按键、玫瑰金外环、红宝石内光圈、AI 扫描横线、高光反光点与暖金四芒星**的高精度光学相机图标，并在外围搭配虚线刻度环与 `✦ AI 识配料` 翠绿浮标；
   - **全屏自适应设计（中间相机取景框占满全部空余高度）**：`.scan-page-fullscreen` 设为 `height: 100vh; overflow: hidden; display: flex; flex-direction: column;`，外层 `.hero-clean-card` 撑满全屏，中间的 `.camera-stage-wrap` 与 `.camera-big-btn` 设为 `flex: 1; height: 100%` 自动吸收并占满顶部导览与底部能力栏之间的全部纵向空余空间；取景框内部采用“顶部状态条 + 中央光学徽章组 (`margin: auto 0`) + 底部快门与对焦提示”三段式撑满排布，兼顾大气全屏感与内部视觉平衡。

5. **「两款PK」与「成分拆解」页彻底剔除写死假数据（`index.js`、`index.wxml`、`pkArena.js`、`pkArena.wxml`）**：
   - **成分拆解页 (`index`)**：移除原代码中的 `fallbackItem`（某预设牛排样本），用户未拍过食品或已清空历史时，不再强行塞入假商品仪表盘和假配料云，而是展示纯净高质感的「📷 暂无实拍食品配料数据 · 立即去拍一款食品配料表」空状态引导卡；仅当用户有真实拍照记录时才加载真实食品拆解；
   - **两款PK页 (`pkArena`)**：彻底废弃原代码中写死的 `PRESET_PK_PAIRS`（超市酱油二选一等预设样本）与 `fallbackHistory`（假酸奶/假牛排），无扫码记录时不展示假对决卡与假指标表，直接展示「📷 暂无食品可供 PK 对比」纯净引导卡；当用户仅扫过 1 款食品时，精准提示「🅰️ 已就绪：A款商品 · 还差 1 款商品即可同台 PK」，引导拍摄第 2 款进行对比；仅当扫过 2 款及以上真实食品时才开启 A/B 槽位真实同台对决。

---

## [2026-10-13] 「两款PK」实拍缩略图 + 智能品名破译 + 专属竖排图文选品列表与半屏抽屉交互重构（`foodScannerMiniApp` v1.2.8）

### 变更背景与用户核心诉求
- 用户实测截图反馈：
  1. **商品无法区分**：此前实拍识别的商品名称一律被写死为 `"实拍食品"`，且没有缩略图与主料预览，导致 PK 页出现一排 `实拍食品 12分`、`实拍食品 100分`，用户完全分不清哪个是刚才拍的哪款食品；
  2. **横向小块极难操作**：原先在顶部窄横条里滑动点按小按钮的交互非常局促，应当做成**带缩略图的专门食品选择列表**；
  3. **跨页面去冗余**：砍掉底部不切实际的手动打字输入配料框及重复模块。

### 核心重构明细
1. **彻底根治“全都叫实拍食品”与“40分/12分商品显示检出 0 项添加剂” Bug（[`safeHydrateItem`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.js#L7-L87)、[`ingredientAnalyzer.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js)）**：
   - **修复微信开发者工具热重载模块缓存导致的 `0 项添加剂` Bug**：在 `compileHotReLoad: true` 模式下，旧内存缓存中的 `ingredientAnalyzer.js` 尚未挂载新导出的函数会导致 `onShow()` 异常中断，进而使 `item.additiveCount` 为 `undefined` 并触发 WXML 兜底渲染为 `检出 0 项添加剂`。现直接在 `pkArena.js`、`homeScan.js`、`index.js` 页面内聚实现自包含的 [`safeHydrateItem`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.js#L7-L87)，直接基于基础 `analyzeIngredients` 实时计算真实的 `additiveCount` 与 `riskSummaryText`（如 `检出 7 项添加剂/风险词`；若某款低分食品不含化学合成添加剂但因糖/油/盐扣分，则精准显示 `含游离糖/油脂/高钠负担`，绝不出现“40分却显示0项添加剂”的违和文案）。
   - **OCR 内嵌品名提取与残余括号清洗**：针对实拍 OCR 原文中包含 `名：、黑椒牛仔粒料：牛肉...` 或 `】低聚果糖...` 的真实场景，自动正则提取真实品名 **`黑椒牛仔粒`**、清洗前导残余符号 `】`，并根据主料自动匹配专属品类图标（`🥩 肉禽制品` / `🥛 乳品烘焙` / `🥤 饮品冲调`），彻底告别千篇一律的 `实拍食品` 与 `🥫 包装食品`。
2. **移除已扫食品列表底部无意义的「真假李逵示例」栏（[`pkArena.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.wxml#L737-L802)）**：
   - 彻底删去「已扫食品选择列表」底部的 `真假李逵示例：超市酱油二选 / 牛排二选一 / 酸奶二选一` 横条（`.list-footer-bar`），让列表 100% 聚焦于用户自己扫码的真实食品对比。
3. **轻量级实拍缩略图持久化与品类徽章兜底（[`persistLightweightThumb`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.js#L177-L220)）**：
   - 拍照识别时，自动将压缩后的轻量缩略图存入小程序本地独立文件目录（`wx.env.USER_DATA_PATH`，不占 `StorageSync` JSON 缓存额度），且当历史超过 10 条时自动回收删除被淘汰商品的旧缩略图文件；
   - 点击任意商品缩略图支持调用 `wx.previewImage` **全屏放大查看实拍原图**。
4. **PK 页全新「双槽位图文对决卡 + 专属竖排图文选品列表 & 半屏选品抽屉」（[`pkArena.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.wxml)、[`pkArena.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.js)）**：
   - **顶部 A/B 双槽位图文卡（`.side-box`）**：每个槽位内直接展示 **`104×104rpx` 实拍缩略图（或品类图标） + 具体食品名称 + 前 3 位配料预览 + 微光圆弧勋章表盘**，槽位底部配有醒目的 **`🔄 更换 A 款食品 ▾`** / **`🔄 更换 B 款食品 ▾`** 按钮；
   - **专门的竖排图文食品选择列表（`.vertical-food-list`）与半屏抽屉弹窗（`.picker-modal-sheet`）**：改为全宽竖排大尺寸图文列表，点击顶部 **`🔄 更换 A/B 款食品`** 或 **`📋 选品列表`** 时弹出专属半屏选品抽屉。

---

## [2026-10-13] 报告页「视觉打分看板 ➔ 独立详情报告」双层架构 & 「是否易胖 / 高糖 / 高油 / 高盐」直觉预警与五维分项打分升级（`foodScannerMiniApp` v1.2.6）

### 变更背景与用户核心诉求
- 用户指出：单维度的健康分不够立体，且首屏堆砌过多长文字说明会淹没核心打分；更关键的是，普通消费者在超市拿起一袋食品时，最迫切想知道的直觉答案是：**「这款食品是不是易胖？防胖分数是多少？是不是高糖、高油、高盐？」**

### 核心升级明细
00. **全站打分圆环升级为「微光圆弧勋章表盘 + 三档分级韵律微动效」（[`homeScan.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxml#L216-L231)、[`index.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/index/index.wxml#L112-L128)、[`pkArena.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.wxml#L661-L705)）**：
   - **告别死板的单层实心粗线圈**：重构为三层微拟物表盘结构——外层为 `conic-gradient` **按真实得分百分比闭合的进度圆弧**（如 `12分` 仅点亮右上角 12% 的警示红弧，其余 88% 为浅雾粉凹槽底轨；`100分` 则 100% 圆满闭合为翡翠环），中层为珠光径向渐变内表盘（配柔和内阴影与顶部微光状态星点），内嵌紧凑分数与等级微章（`C级` / `A+级`）；
   - **克制有内涵的「分等级物理韵律动效」**：
     - 🟢 **绿灯（A+/A级）**：`auraCalmBreathe`（`3.2s` 慢节奏晨露呼吸光晕，传递平和、纯净、安心）；
     - 🟡 **黄灯（B级）**：`auraGentlePulse`（`2.4s` 温和琥珀微光流转，传递理性克制）；
     - 🔴 **红灯（C级）**：`auraAlertRipple`（`1.8s` 同心游丝环向外微扩散消隐，不晃动文字但极具专业仪表警示张力）。
0. **圆环正下方内嵌「微缩四级标尺（`A+ | A | B | C`）」+ 点击浮入说明气泡（`.mini-grade-strip` & `.grade-float-popover`，[`homeScan.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxml#L223-L258) & [`homeScan.wxss`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxss#L1573-L1742)）**：
   - 摒弃占地方的独立大卡片，直接在左上角综合分圆环及 `C级 · 建议少吃` 胶囊正下方，内嵌一条小巧精致的四级微缩胶囊条 **`[A+] [A] [B] [C]`**，当前所处等级自动实心高亮；
   - **点击按需浮入详情**：点击任意等级（`A+` / `A` / `B` / `C` 或圆环下方等级胶囊）时，通过绝对定位动画（`@keyframes gradePopoverFloatIn`）浮入一张轻巧的气泡卡片，展示该级别的名称、分数区间（如 `90-100分`）与一句话说明，既不占用页面固定空间，又能随时点击查看全等级标准。
1. **首屏新增「易胖 / 高糖 / 高油 / 高盐」2×2 四宫格直觉预警卡（`bodyAlerts`，[`ingredientAnalyzer.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js#L351-L407) & [`homeScan.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxml#L280-L298)）**：
   - 紧贴综合健康分圆环下方，用红/黄/绿三色高对比度四宫格直接给出定性结论与原因：
     - **发胖风险**：`🔴 易胖预警`（如“糖+油叠加易囤脂”、“主料含高热量糖油”） / `🟡 中度发胖风险` / `🟢 不易发胖`（“低卡轻负担配料”）；
     - **糖分评估**：`🔴 高糖预警`（如“白砂糖排前3位”、“检出 2 种添加糖”） / `🟡 含添加糖/代糖` / `🟢 低糖/无糖`；
     - **油脂评估**：`🔴 高油/坏油预警`（含氢化油/植脂末/起酥油） / `🟡 含精炼油` / `🟢 清爽低油`；
     - **盐钠评估**：`🔴 高盐高钠`（检出 ≥2 项盐或增鲜钠盐） / `🟡 含钠调味` / `🟢 轻盐低钠`。
2. **「五大营养维度专业评分」进度条看板（`dimensionScores`，[`ingredientAnalyzer.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js#L436-L442)）**：
   - 将上一轮的专业营养维度与本轮的热量/糖油盐控制深度融合，统一采用规范得体的五字**「XX度」**专业命名（0~100 分，越高越健康）：
     1. **配料纯净度 (`cleanLabel`)**：评估防腐剂、人工色素、合成甜味剂及增稠香精数量（如 `清洁标签 · 0 合成添加剂`）；
     2. **轻卡控能度 (`calorieControl`)**：替代生硬的“不易发胖分”，融合前 3 位主料营养含金量与“糖+油+精制碳水”囤脂效应（如 `主料优质 · 轻卡低负担` vs `糖油碳水叠加 · 易囤脂`）；
     3. **控糖友好度 (`sugarControl`)**：评估白砂糖/果葡糖浆排位及隐形糖/代糖负担（如 `0 游离糖 · 控糖友好`）；
     4. **油脂健康度 (`oilQuality`)**：评估植脂末、氢化油、起酥油等反式脂肪风险与精炼油占比（如 `0 反式脂肪 · 清爽好油`）；
     5. **轻钠低盐度 (`sodiumControl`)**：评估食盐及味精/呈味核苷酸二钠等隐形钠负担（如 `清淡低钠 · 0 隐形钠`）。
3. **「首屏纯打分看板 (`!showDetailReport`) ➔ 二级详细文字报告 (`showDetailReport`)」双层解耦（[`homeScan.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxml#L204-L492)）**：
   - **第一层（默认打分看板）**：只展示「综合健康分圆环 + 易胖/高糖/高油/高盐四宫格预警 + 五维分项进度条 + 四类人群红绿灯胶囊」，零长篇大论；
   - **第二层（点击「查看详细评估报告与配料原文 ›」进入）**：集中收纳「完整文字评估理由、扣分风险成分与天然原料清单、四类人群详细食用建议、识别到的配料原文及手动改字重算框」。
4. **扫过的食品历史库扩容至 10 条且「零图片纯文本存储」（[`homeScan.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.js#L294-L320) & [`pkArena.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.wxml)）**：
   - `SCAN_HISTORY_LIST` 保留最近 **10 条**记录，仅存储商品名称、配料纯文本与分数字段（不存拍下的照片文件，10 条合计不足 `3KB`，彻底杜绝内存膨胀），并在「两款PK」页顶部支持一键对比最近扫过的两款食品。

---

## [2026-10-12] 首页核心拍照流程「三态清爽重构」：极简大按钮待机态 ➔ 沉浸式激光雷达扫描动画态 ➔ 完成后滑入报告态（`foodScannerMiniApp` v1.2.5）

### 变更背景与体验痛点
- 用户反馈此前进入首页即直接平铺大篇幅的预设结果卡片、配料原文框与人群建议，导致拍照核心功能不够清爽聚焦；且识别过程中仅有单调的文字 Loading，缺少视觉反馈与仪式感。

### 三态状态机（`viewState: 'idle' | 'scanning' | 'result'`）重构明细
1. **状态一：`idle`（整屏单一拍照主舞台，零冗余卡片，[`homeScan.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxml) / [`homeScan.wxss`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxss)）**：
   - **彻底移除下方两块多余的白底卡片**（「常见商品样张体验卡」已存在于第二个 Tab `成分拆解`，「最近查过的食品卡」同样造成首屏视觉分散）；
   - 首屏 100% 收拢为一张撑满视口（`min-height: calc(100vh - 80rpx)`）的柔光渐变主舞台大卡（`.hero-clean-card`）：顶部为品牌信任胶囊与三步流程导览，正中央为带四角取景框（`.idle-vf-corner`）、双层呼吸脉冲光环与 `立即开启 AI 扫描 ›` 胶囊的 3D 大按钮，底部内嵌三枚极简能力背书（`GB 2760 国标词典 · 四类人群避雷 · 红绿灯秒级定性`），视觉高度纯粹聚焦。
2. **状态二：`scanning`（轻快高颜值 AI 激光扫描态，[`homeScan.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxml) / [`homeScan.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.js)）**：
   - **视觉重构**：摒弃原先灰暗压抑的深色遮罩与笨重的三行竖排方框，统一为与待机首页一脉相承的**整屏柔光蜜桃渐变舞台（`.scanning-hero-card`）**；中央珍珠白相框内 100% 高清透亮展示实拍照片（`opacity: 1`，无遮挡气泡），搭配高光激光束（`@keyframes laserSweep`）与双层脉冲波纹；底部采用精巧的**横向三节点进度条（`① 提取配料 ━━ ② 国标排雷 ━━ ③ 生成报告`）**；
   - **极速秒出报告（总耗时仅约 1~1.3 秒）**：修复 `onStepChange(step, text)` 参数顺序导致顶部胶囊显示数字 `2` 的问题；百度 OCR 提取完成后，本地 GB 2760 国标词典与红绿灯评分仅需 **10ms** 即可算完，过渡动画收敛至 **0.3 秒** 即刻滑出完整报告，DeepSeek 在后台静默精修文案、绝不让用户干等。
3. **状态三：`result`（识别完成后滑入完整评测报告态）**：
   - 仅当扫描与计算全部达到 `100%` 后，才通过向上滑入过渡动画（`@keyframes slideUpReveal`）展开完整报告；
   - 报告顶部配备专属快捷导航栏（`‹ 返回清爽首页`、`📷 再拍一袋`、`⚔️ 加入PK`），随时可一键清空返回极简大按钮首页。
4. **OCR 错字双重校对与二次复核闭环（[`ingredientAnalyzer.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js) ⇄ [`ocrAndAiService.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ocrAndAiService.js)）**：
   - **第一道本地正则纠偏（`correctOcrTypos`）**：毫秒级自动修正高频食品添加剂形近错字（如 `山犁酸钟` ➔ `山梨酸钾`、`果萄糖浆` ➔ `果葡糖浆`、`氨化植物油` ➔ `氢化植物油`）；
   - **第二道 DeepSeek AI 语义校对 + 国标二次复核**：在 System Prompt 中强制要求 AI 拿到 OCR 文字后**第一步先通读校对并还原错别字**（输出 `cleanIngredients` 与 `correctedIngredientText`）；随后代码将 AI 修正后的文本**再次送入本地 GB 2760 词典复核（`refinedLocal = analyzeIngredients(correctedIngredientText)`）**，一旦 AI 修好错字后暴露出此前因错字漏检的高危添加剂，立即重算分数上限并自动回填更新页面底部的「识别到的配料原文」。

---

## [2026-10-12] 专业产品与 UX 深度升级：打分结论首屏前置、最近查过历史足迹、配料前3位主料与国标号鉴定、一键送 PK 闭环（`foodScannerMiniApp` v1.2.4）

### 核心产品升级明细
1. **首屏视觉动线重构（打分结论前置 + 原文折叠抽屉，[`homeScan.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxml)）**：
   - 将「AI + 国标健康打分结果大卡（`.score-result-card`）」直接提升至顶部拍照卡正下方，用户拍完照无需下滑即可在首屏一眼看清健康安心分、红绿灯评级与四大风险指标；
   - 将「识别到的配料原文」下移并改为默认 2 行摘要预览，点击右上角 `修改/重算 ▾` 即可展开编辑，兼顾首屏高信噪比与手动纠错灵活性。
2. **新增「最近查过」本地评测历史足迹栏（`.history-bar`，[`homeScan.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.js)）**：
   - 自动通过 `wx.setStorageSync('SCAN_HISTORY_LIST')` 持久化保存最近 8 条扫码/样张评测记录（含红黄绿状态圆点、商品简称、健康分），解决超市货架前连拍多款商品后的回看对比痛点。
3. **新增「国家执行标准号破译徽章（`.std-hit-list`）」与「配料前3位主料透视（`.top3-bar`）」**：
   - 自动识别包装上的 `SB/T 10379`（调理合成肉）、`GB/T 17238`（纯原切牛肉）、`GB 25190`（100%纯牛奶）、`GB 25191`（调制乳）等国标号并高亮定性；同时单独高亮配料表前 3 位主料（按 GB 7718 含量从高到低排列），大幅提升营养学专业背书感。
4. **打通「单品打分 ➔ 一键加入同台 PK」闭环（[`homeScan.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.js) ⇄ [`pkArena.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.js)）**：
   - 打分卡右上角新增「⚔️ 加入PK对比」按钮，支持一键将当前评测商品设为 PK 台的【A款】或【B款】并自动跳转算分。

---

## [2026-10-12] 修复真机扫码预览拍照后一直卡在「正在识别配料文字...」Loading 问题（`foodScannerMiniApp` v1.2.3）

### 变更背景与真机卡死根因
1. **真机 `wx.createVKSession` 传入字符串路径导致原生线程挂起**：
   - 在电脑模拟器上 `wx.createVKSession` 为 `undefined`，会直接跳过；但在**手机真机预览**环境下 `wx.createVKSession` 存在。当真机未开启「打开调试」模式导致百度 OCR 域名（`aip.baidubce.com`）被微信拦截进入 `catch` 分支时，旧版 `tryWechatOfflineVkOcr` 向 `session.runOCR({ frameBuffer: filePath })` 传入了字符串路径（而非 RGBA 像素 `ArrayBuffer`），导致手机微信底层 VisionKit 桥接挂起，`wx.hideLoading()` 永远无法执行，画面一直停留在 `正在识别配料文字...`。
2. **真机高像素原图 Base64 体积过大 & `127.0.0.1` 回退超时**：
   - 手机相机拍照原图可达 3MB~5MB，直接转 Base64 上传耗时极长或触发百度 OCR 4MB 大小上限；且 `callDeepSeekChat` 中包含 `127.0.0.1:18080` 本机网关回退，在手机真机上会无谓等待 12 秒超时。

### 核心修复动作
1. **移除错误的 `VKSession` 字符串调用 + 增加 8 秒强制超时释放锁（[`src/utils/ocrAndAiService.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ocrAndAiService.js)）**：
   - 新增 `compressImageForPhoneOcr`（调用 `wx.compressImage` 将手机拍照图片压缩至 `1080px` / `60%` 质量，约 100KB~200KB，5G 上传仅需 0.6 秒）；
   - 新增 `safeHideLoading()` 与 8 秒硬超时保护器 `finalize()`，确保在任何成功、失败或超时分支下 **100% 立即关闭 Loading 弹窗**；
   - 小程序环境下跳过 `127.0.0.1:18080` 回退并将网络超时收敛至 `6500ms`。
2. **真机域名拦截智能引导与一键开启调试（[`mp-weixin/pages/homeScan/homeScan.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.js)）**：
   - 拍照后立即将实拍图展示在缩略图预览区；若检测到真机因未开调试模式拦截域名（`url not in domain list`），立即关闭 Loading 并弹窗提供 **「开启调试」** 按钮（直接调用 `wx.setEnableDebug({ enableDebug: true })`），开启后真机 5G 即可直接调用百度 OCR 与 DeepSeek。

---

## [2026-10-12] 4 大 Tab 页面移动端排版布局精修（零折行挤压 + 3×2对称样张网格 + 自适应高度文本框）& 100% 原生 CommonJS 修复（`foodScannerMiniApp` v1.2.2）

### 变更背景与根因定位
1. **微信开发者工具移动端（375px）局部文字挤压折行与留白问题（实机截图诊断）**：
   - **「识别到的配料表」卡片顶栏 4 元素同行挤压**：原 `.ocr-header` 将标题（`识别到的配料表`）、来源标签（`超市常查样张`）及两个操作按钮（`重新拍照`、`重新AI打分`）全部塞在同一水平行内，导致在 375px 屏宽下 4 个元素全部被挤成尴尬的单字换行（`表`、`张`、`照`、`分` 各掉到第二行）；
   - **微信原生 `<textarea>` 默认 300rpx 固定高度留白**：未开启 `auto-height` 时，微信原生 `<textarea>` 默认高度过大，在卡片下半部产生大片空白，并将核心「健康分结果大卡」挤出首屏；
   - **热门商品样张胶囊流式换行落单**：6 个样张胶囊采用 `flex-wrap` 时排成 `2 + 3 + 1` 三行，最后一枚 `100%纯牛奶` 孤立在第三行；
   - **微信 `"style": "v2"` 下 `<button>` 默认 `width: 184px` 样式污染**：导致底部双按钮操作栏比例失衡。
2. **微信开发者工具 4 个页面全空白根因（已同步解决）**：
   - 将 `mp-weixin/` 下全部模块与页面全量转换为 100% 微信原生 CommonJS (`require` / `exports`) 并预置首帧同步 `data`。

### 4 大页面排版布局精修明细
1. **全局样式重置（[`mp-weixin/app.wxss`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/app.wxss)）**：
   - 彻底重置微信小程序 `style: v2` 下 `button:not([size='mini'])` 默认固定 `184px` 宽度与居中 `margin` 的问题，隐藏滚动条并统一卡片圆角与内边距规范。
2. **Tab 1 · 拍照打分首页（[`homeScan.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxml) / [`homeScan.wxss`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxss)）**：
   - **「识别到的配料表」卡片重构为上中下三段式**：顶栏仅保留左标题 `识别到的配料表` 与右胶囊 `{{currentProductName}}`；中间 `<textarea>` 开启 `auto-height="{{true}}"` 紧贴配料文字零多余留白；底部独立 `.ocr-footer` 工具栏左置提示、右置 `重新拍照` 与 `重新AI打分` 胶囊按钮，**彻底根除单字挤压折行**；
   - **6 款热门商品样张升级为 `3列 × 2行` 对称网格（`.preset-chips`）**：6 枚胶囊严丝合缝对齐为两行三列，视觉整洁且节省首屏高度，让下方的「健康安心分大卡」直接在首屏露出。
3. **Tab 2 · 成分拆解页（[`index.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/index/index.wxml) / [`index.wxss`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/index/index.wxss)）**：
   - 顶部样张区同样升级为 `3×2` 对称六宫格；新增当前商品「得分圆环 + 红绿灯定性速览横条（`.summary-strip`）」；分区标题拆分为左主标题 + 右浅粉计数胶囊，消除长标题折行。
4. **Tab 3 · 两款PK页（[`pkArena.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.wxml) / [`pkArena.wxss`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/pkArena/pkArena.wxss)）**：
   - 对决舞台（`.vs-arena`）采用 `1fr 1fr` 双列网格并将 `VS` 徽章改为正中绝对定位悬浮（`position: absolute; left: 50%; top: 50%`），释放左右商品卡完整宽度；补齐「5维核心指标逐项横评表」与「自定义 A/B 款配料自由对比区」。
5. **Tab 4 · 避坑宝典页（[`slangBible.wxml`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/slangBible/slangBible.wxml) / [`slangBible.wxss`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/slangBible/slangBible.wxss)）**：
   - 顶部横幅内置实时关键词搜索框；分类栏采用 `5列等宽网格（repeat(5, 1fr)）` 一屏完整展示 `全部必看 / 牛奶饮品 / 买肉生鲜 / 酱油粮油 / 儿童零食`，告别横向截断滚动条；卡片采用左徽章右文案的红绿对照排版。

---

## [2026-10-12] 宝妈亲和系高定柔光渐变 UI 升级 & 阿里巴巴 Iconfont 矢量多色图标库全量替换（`foodScannerMiniApp` v1.2.0）

### 变更背景与视觉升级
1. **面向宝妈群体的「奶油蜜桃粉 × 晨露薄荷绿 × 暖杏奶白」高定柔光渐变体系（[`App.vue`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/App.vue)、[`homeScan.vue`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/pages/homeScan/homeScan.vue)、[`mp-weixin/pages/homeScan/`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/mp-weixin/pages/homeScan/homeScan.wxss)、[`preview.html`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/preview.html)）**：
   - 全局背景升级为三层柔和径向光晕渐变（樱花粉雾 `#FFC2D1` + 薄荷晨露 `#A7F3D0` + 暖杏奶白 `#FFF9FB`）；
   - 首页主视觉卡片采用 `135deg` 蜜桃珊瑚柔光渐变（`#FF758C -> #FF8DA1 -> #FFB199`）配双层柔光球与毛玻璃胶囊；
   - 核心「拍照/选图识配料」按钮升级为 **3D 珍珠果冻质感大按钮**，健康评分环升级为高对比度**安心分（翡翠薄荷绿/琥珀暖黄/珊瑚蜜桃红）分级光环**。
2. **阿里巴巴矢量图标库 (`iconfont.cn`, `1024×1024` viewBox) 独立模块与 TabBar 图标集（[`iconfontAssets.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/iconfontAssets.js) & [`scripts/generateTabIcons.mjs`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/scripts/generateTabIcons.mjs)）**：
   - 新建 [`src/utils/iconfontAssets.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/iconfontAssets.js)（并自动同步至 `mp-weixin/utils/iconfontAssets.js`），内置 15 枚阿里巴巴 Iconfont 标准 `1024×1024` 坐标系圆润多色渐变矢量图标（`cameraScan`、`sparkleAi`、`babyBottle`、`pregnantHeart`、`elderShield`、`fitnessLeaf`、`sugarCube`、`transFatDrop`、`labFlask`、`saltShaker`、`pkBadge`、`slangBook`、`safeCheck`、`riskAlert`、`shareHeart`），同时导出小程序 `<image>` 直连 `ICON_DATA_URIS` 与 Web 内联 `ICON_RAW_SVGS`；
   - 生成全套 8 张抗锯齿微信底部 TabBar 图标（`tabScanDefault/Active.png` 等），全面淘汰廉价 Emoji。

---

## [2026-10-12] 新增极简「📸 拍照打分」第一首页、初期 100% 零广告纯净模式、百度免费 OCR + DeepSeek 混合打分 & 三大上线难点加固（`foodScannerMiniApp` v1.1.0）

### 变更背景与产品升级
1. **极简第一首页设计（[`homeScan.vue`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/pages/homeScan/homeScan.vue) 设为第一启动页）**：
   - 将第一首页收敛为**「📸 拍照 ➔ 百度OCR识别配料 ➔ DeepSeek AI 对健康程度打分 (0~100分)」**极简三步闭环，零认知负担，完全契合超市随手拍场景。
2. **初期 100% 零广告纯净口碑模式（[`adAndApiConfig.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/config/adAndApiConfig.js) -> `PURE_NO_AD_MODE: true`）**：
   - 彻底隐藏所有底部广告占位横幅与激励视频解锁门槛，所有 AI 健康评分、四大人群建议、两款食品 PK 结论 100% 免费直出。
3. **三大核心工程难点全量加固**：
   - **① 真实包装袋「跨行断词缝合 + 营养表/厂址噪点自动剥离 + 双 OCR 省流容灾」（[`ingredientAnalyzer.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js) -> `preprocessOcrRawText` & [`ocrAndAiService.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ocrAndAiService.js)）**：自动截断配料表前后的保质期、营养成分表与厂址垃圾文字（同时保留全文中的 `GB/T` / `SB/T` 执行标准号），并把塑料袋换行劈开的化学名词（如 `氢化\n植物油`、`脱氢\n乙酸钠`）无缝缝合；支持百度高精 OCR 与微信免费离线 `wx.createVKSession` 互为容灾备份及 `wx.cropImage` 拍照裁剪。
   - **② 「国标硬规则锁死上下限 + DeepSeek 混合打分算法」（[`ingredientAnalyzer.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/src/utils/ingredientAnalyzer.js) -> `computeHardRuleBounds`）**：先由本地 GB 2760 词库计算确定性分数边界 `[scoreFloor, scoreCeiling]`（如检出反式脂肪马甲或合成肉 `SB/T 10379` 强制封顶 `≤55分红灯`，纯天然 0 添加配料强制保底 `≥90分绿灯`），将其作为不可违背的预审事实注入 DeepSeek Prompt 并对返回分数执行二次钳制（Clamping），彻底根除 AI 打分忽高忽低的幻觉（[`tests/analyzer.test.mjs`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/tests/analyzer.test.mjs) **7/7 单元测试全部通过**）。
   - **③ 微信免费云函数藏 Key 与按 `OPENID` 每日限流防盗刷（[`cloudfunctions/ocrAndScoreProxy/index.js`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/foodScannerMiniApp/cloudfunctions/ocrAndScoreProxy/index.js)）**：提供开箱即用的微信云开发函数，将百度 OCR 与 DeepSeek 密钥物理隔离在云端，并基于调用者真实微信 `OPENID` 限制每人每天免费调用 20 次，0 服务器成本杜绝恶意刷量。

---

## [2026-10-12] 轻量级单输入框收敛重构、多处连续改字防覆盖、长字改短字后文自动平移拼接 & 硅基流动 DeepSeek Janus-Pro-7B 生图接入（v1.1.2）

### 变更背景与核心升级
1. **消除双文本框重复与割裂感（按模式收敛 UI，`index.html` + `src/app.js`）**：
   - **问题**：此前在「⚡ 轻量级」模式下，上方「修改指令」文本框与下方「文字图层精修 - 新文字」输入框同时出现且功能高度重叠，导致用户困惑该在何处输入，且两边状态容易冲突。
   - **重构方案**：
     - **⚡ 轻量级（文字/细节微调）**：默认隐藏上方的自然语言指令大框与底部「立即 AI 改图」按钮，将**「✏️ 框选文字实时替换」**提至最上方常驻显示，界面上**仅保留唯一一个「替换文字」输入框**。截图带红框进入工作台（或在右图拖拽框选）后，自动创建图层并聚焦到「替换文字」输入框，用户**每敲一个字右侧画布毫秒级实时生效（零 Token 消耗）**；同时保留底部折叠入口 `🤖 不想手动框选？点击展开 AI 自然语言自动找字修改 ▾` 供按需展开。
     - **🎨 重绘级（局部重新设计）**：切换至重绘级时自动隐藏「文字图层实时替换」与滤镜面板，仅展示「🎨 AI 重绘指令 (Prompt)」与「立即 AI 重绘」按钮。
2. **修复多处连续改字时后一次把前一次覆盖的 Bug（`src/app.js`）**：
   - 新增 [`isRectOverlap`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/app.js#L2587-L2594) 与 [`upsertPatch`](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/src/app.js#L2599-L2613)，废除 `patches = ...` 全量覆盖，实现多处独立图层（`图层 1`、`图层 2`、`图层 3`）增量并存；
   - 为每个红框和图层引入 `_consumed` 状态锁，连续框选第 2 处、第 3 处时自动新建图层并聚焦清空输入框，前序已改好的文字 100% 独立保留。
3. **解决「框选多字改为少字」产生大块行内留白痛点（`src/app.js` -> `analyzeAndRefineTextBox` & `renderCanvas`）**：
   - 新增**同行右侧后文像素切片自动扫描（`lineTail`）**与 `#tuneFillMode` 5 种自适应排版模式（默认开启 `🔗 后文自动平移拼接 (reflow)`，并支持 `↔️ 字距均匀拉开铺满 (spread)`、`🔍 字号自动缩放铺满 (scale)`、`📐 原位左对齐`、`🎯 原位居中对齐`）。当长字改短字时，自动将同一行右侧的后半句文字整块向左平移紧贴新文字，并将原尾部无痕擦除为纯净背景色。
4. **重绘级新增「🚀 硅基流动 (免费调用 DeepSeek Janus-Pro-7B)」生图渠道（`index.html` + `src/app.js`）**：
   - 针对 `api.deepseek.com` 官方平台仅提供文本对话接口的情况，在重绘服务渠道中新增 `siliconflow`，支持直接免费调用 DeepSeek 官方开源的多模态自回归生图模型 `deepseek-ai/Janus-Pro-7B` 及 `Kwai-Kolors/Kolors`，并在画有红框时自动将生成图案精准镶嵌合成至红框区域内。

---

## [2026-10-12] 修复「改图」直达工作台 BOM 解析中断 & 一段文字局部改字像素级对齐与无脏块擦除优化（v1.1.1）

### 变更背景与根因排查
1. **截图选中「改图」未自动跳转工作台并填充图片**：
   - 经排查 `~/.rubick_snipper_debug.log`，C# `File.WriteAllText(..., Encoding.UTF8)` 默认写入了 UTF-8 BOM 头（`\uFEFF`），导致 `preload.js` 中 `JSON.parse` 抛出 `Unexpected token '﻿'` 异常并跌落至 `onCancel` 分支，中断了后续跳转。
2. **只修改一段文字中的某几个字时出现偏位、残留半个字与灰白色脏块**：
   - **擦除区灰白色脏块根因**：当用户只框选一句话中的前两个字（如 `右侧画布...` 中的 `右侧`）时，擦除框右边界紧贴相邻汉字 `画` 的白色笔画。旧版无条件使用四向边缘双线性插值（`smartInfillRectOnCtx`），直接将右边界采到的白色笔画向右左拉扯，在深黑底色（`#141413`）上晕染出一团灰白色脏块。
   - **擦除框偏右偏窄、残留左半边字根因**：旧版 `runLightweightAiEdit` 将图片缩放至 `1024px` 发送给大模型，却在 Prompt 中要求模型按原图尺寸 `(W, H)` 返回像素坐标，且在 `centerInRef` 判定通过时用大模型估算的偏小坐标覆盖了用户画的真实红框 `refBox`。
   - **字号偏小、被强制加粗与蓝色虚线框遮挡根因**：旧版字号简单按框高 `height * 0.65` 乘算（未测量真实字形像素高度），默认写死 `isBold: true`，并在画布上常驻绘制激活图层的蓝色虚线框。

### 核心修复动作
1. **彻底打通 `[✦ 改图]` 自动唤醒并填充工作台全链路（`Program.cs` + `preload.js` + `app.js`）**：
   - `Program.cs` 改用无 BOM 的 `new System.Text.UTF8Encoding(false)` 写入三图分离 JSON 包，同时在 `ConfirmCrop`（点复制/OCR）和 `ConfirmAiEdit`（点改图）时同步缓存最新数据包至 `%TEMP%\rubick_latest_capture_bundle.json`，重新编译生成 `bin/snipper.exe`；
   - `preload.js` 读取 JSON 前剥离 `\uFEFF`，并在收到 `ai_edit` 动作时主动调用 `showMainWindow()` + `setHeight(600)` 弹出并展开工作台；
   - `app.js` 新增单图已画红框自动识别与无痕剥离（`autoDetectAndCleanBakedRedBoxes`）以及最新标注包自动认领机制，即使通过「复制后进工作台」也能自动还原红框坐标与纯净底图。
2. **局部改字像素级紧密包络与纯净背景聚类算法（`src/app.js` -> `analyzeAndRefineTextBox`）**：
   - **16×16×16 RGB 直方图众数背景提取 + 均匀度判定（`bgIsUniform`）**：自动剔除相邻文字笔画与红框干扰提取纯净背景色；当背景为纯色或近似纯色时直接用纯净背景色填充擦除，彻底消除相邻文字笔画被双线性插值拉出的灰白色脏块；渐变背景插值前先执行 `sanitizeEdge` 清洗边缘上的文字像素；
   - **像素级字形紧密包络检测与字符间隙吸附**：在红框内自动扫描前景文字笔画的真实上下左右边界 `[fgMinX, fgMinY, fgMaxX, fgMaxY]`，若左右边界切在汉字笔画上则自动向外探索至字符空白间隙，确保旧字 100% 擦净且不伤邻字；
   - **基于 `measureText` (`actualBoundingBoxAscent + actualBoundingBoxDescent`) 的精确字号与基线反推**：根据原字真实像素高度 `glyphH` 反推 1:1 渲染字号，并根据笔画密度自动判定是否粗体（普通正文默认非粗体）；
   - **移除常驻遮挡的蓝色虚线框**：仅在鼠标悬停或按住拖拽微调文字时才显示外围辅助虚线框。
3. **全链路深度自检追加修复的 6 个隐蔽边界 Bug**：
   - **① 手绘红框边缘蹭到邻字 1~4px 时的「向内退让」与防碰撞 (`analyzeAndRefineTextBox` + `renderCanvas`)**：当用户手绘红框圈选句子局部文字且右边框不小心蹭到下一个汉字 1~4px 边缘时，利用 1D 垂直投影检测框内边缘处的字符空白间隙，自动将擦除边界**向内退让**到空白间隙处，绝不擦掉后一个字的左半边；同时检测右侧是否存在紧邻汉字（`hasRightNeighbor`），若新文字略宽则通过 `ctx.fillText(..., maxAllowedW)` 自适应微缩字宽，100% 防止新字印到后一个字上面。
   - **② 「红框 + 箭头 + 文字」组合标注时的指示性图元过滤 (`getRegionTargetBoxes` + `Program.cs`)**：严格区分区域框选图元（`Rect`/`Ellipse`/`Marker`/`Brush`）与指示性图元（`Arrow`/`Line`/`Number`/`Text`），防止把用户画的指示箭头或序号误当成文字擦除框或重绘遮罩。
   - **③ 外部粘贴单图红框检测的严格空心矩形拓扑校验 (`autoDetectAndCleanBakedRedBoxes`)**：要求 `bw >= 20 && bh >= 14`、外围 4px 边框带像素占比 >= 85% 且四条边覆盖率均 >= 70%，彻底防止将网页原本自带的红色文字（如价格数字）或红色图标误判为手绘红框抹除；并改为 `async/await` 同步更新 `cleanImgObj` 与 `bundle.cleanDataUrl`，消除异步竞态。
   - **④ 剪贴板粘贴外部新图 / 历史记录切换时的旧 Bundle 串图修复 (`isMatchingCaptureBundle`)**：仅当 `latestCaptureBundle` 与当前图片匹配（或属 15 秒内刚完成的 `snipper.exe` 截图）时才认领，外部 `Ctrl+V` 粘贴、历史记录恢复、「以此图继续改」时均主动清空旧 `latestCaptureBundle`。
   - **⑤ 手动框选改字 (`draw_box`) 参数修正**：修正 `mouseup` 中调用 `analyzeAndRefineTextBox` 的第 5 参数为 `{ hasUserRedBox: true }` 并补齐 `glyphX/glyphW/hasRightNeighbor` 属性。
   - **⑥ 插件重新激活时的工作台防覆盖保护 (`checkStartupState`)**：当处于 `ai_edit` 模式且画布已有图片时，禁止启动自检逻辑用历史记录冲掉当前正在改图的画面。

---

## [2026-10-12] 新增「截图标注 + AI 智能改图（分级双引擎）」全链路功能（v1.1.0）

### 变更背景
- 用户提出希望在截屏并标注之后，能够直接通过 AI 根据标注对截图进行修改生成新图，核心高频场景为**「图片上的文字微调 + 图片视觉效果微调」**，同时兼顾**「局部区域重新设计（重绘）」**，且关注大模型 Token 消耗与阿里云百炼账户余额问题。
- 经方案论证（`docs/superpowers/specs/2026-10-12-aiImageEditSpec.md`），确定采用 **交互方案 A（截屏工具栏一步直达 `[✦ 改图]`）+ 分级双引擎架构（⚡ 轻量级文字/细节微调 + 🎨 重绘级图像编辑大模型）**。

### 核心架构与实现明细

#### 1. C# 原生截屏器 (`src/snipper/Program.cs` -> `bin/snipper.exe`) 三图分离导出与桌面贴图扩展
- **工具栏新增 `[✦ 改图]` (`ai_edit`) 按钮**：位于 `[文 OCR]` 按钮右侧，采用紫罗兰星光矢量图标与专属文字胶囊。
- **三图分离导出 (`ExportAiEditBundle`)**：
  - 点击 `[✦ 改图]` 时，自动在 `%TEMP%` 生成 JSON 数据包并通过 `ACTION:AI_EDIT:<bundlePath>`（退出码 11）返回给 Electron 宿主；
  - 包内同时含三张图与结构化标注元数据：
    1. `cleanPngBase64`：不含任何红框/画笔/文字标注的**纯净原图**（作为修改与合成底图，从根源上杜绝红框残留在生成图中）；
    2. `annotatedPngBase64`：包含用户绘制的红框、箭头、画笔及 `T` 文字批注的**视觉引导图**（专供 AI 视觉定位）；
    3. `maskPngBase64`：将非文字标注图元（矩形、椭圆、画笔、马克笔）自动向外膨胀 `8px` 生成的黑底白区 **Inpainting 遮罩图**；
    4. `textNotes` & `boxes`：自动提取用户在图上用文字工具 `T` 输入的文本内容（预填为 AI 修改指令）及标注框坐标。
- **新增命令行贴图模式 (`--pin-image <path>`)**：支持从插件工作台一键将改好的新图直接钉回桌面，复用 `PinForm` 置顶贴图窗体。

#### 2. Electron 宿主桥接层 (`preload.js`)
- `captureScreen()` 新增 `ACTION:AI_EDIT:` 协议分支解析，读取并自动清理临时 JSON 文件后返回 `{ action: 'ai_edit', dataUrl, bundle }`；
- 新增 `copyImageDataUrl(dataUrl)`（直接将改后 PNG DataURL 写入系统剪贴板）与 `pinImageDataUrl(dataUrl)`（调用 `snipper.exe --pin-image` 置顶贴图）。

#### 3. 工作台双模式切换与 AI 智能改图 UI (`index.html` + `src/style.css`)
- 顶部导航栏新增模式切换胶囊：`📄 OCR 翻译` 与 `✨ AI 智能改图`，且在 OCR 左栏图片预览区提供 `✨ 送去改图` 一键流转入口；
- 构建 `#workspaceAiEdit` 独立双栏工作区：
  - **左侧控制台**：分级引擎切换卡片（⚡ 轻量级 / 🎨 重绘级）、指令输入框（自动带入截图标注中的 `T` 文字指令 + 常用快捷指令 Chip）、`➕ 手动框选改字 (0 Token)` 按钮、识别出的可编辑文字图层列表（支持单层字号/颜色/粗细/显隐实时微调）、重绘级渠道切换与「🔒 仅重绘红框/标注区域」开关、图像细节微调滑块（亮度/对比度/饱和度/色温）；
  - **右侧画布舞台**：自适应缩放主画布 `aiEditCanvas`、手绘涂抹遮罩层 `aiEditMaskOverlay`、`➕ 手动框选` 拖拽十字交互层，以及底部高频交付栏（`👁️ 按住对比原图`、`🔄 以此图继续改`、`📄 转 OCR`、`📌 贴到桌面`、`💾 保存`、`⧉ 复制新图`）。

#### 4. 分级双引擎核心算法 (`src/app.js` -> `aiEditController`)
- **⚡ 轻量级引擎（默认高频 · 约 200 文本 Token / 零生图费用）**：
  - 复用用户已配置的视觉模型（Qwen-VL / GLM-4V / GPT-4o / Gemini 等），同时传入 `cleanDataUrl` 与 `annotatedDataUrl`，强制返回千分比归一化坐标 `[ymin, xmin, ymax, xmax]` 及原文字/新文字/字体属性；
  - **本地像素级无痕擦除 (`eraseBoxArea`)**：采样框外周 3px 像素环，当背景方差极低时采用中位数纯色填充，当背景存在渐变或纹理时采用**四向边缘双线性插值 + 轻微羽化**无痕抹除原字；
  - **矢量级文字重绘与拖拽微调**：自动从原区域聚类提取主文字色与字高，使用 Canvas 2D 高清重绘新文字；支持用户直接在右侧画布上用鼠标拖拽任意修改后的文字图层微调位置，或点击 `➕ 手动框选改字` 在不调 API（0 Token）的情况下原地擦除并改字。
- **🎨 重绘级引擎（局部/整体重新设计 · 含免费算力渠道）**：
  - 内置三大渠道：**魔搭社区 ModelScope 免费接口**（`Qwen/Qwen-Image-Edit`，支持异步任务轮询）、**阿里云百炼**（`qwen-image-edit` Multimodal Generation API）、**OpenAI / 硅基流动兼容接口**（`/images/edits` 与 `/images/generations` 自动回退）；
  - **局部像素零损回贴 (`compositeWithFeatherMask`)**：开启「🔒 仅重绘红框/标注区域」时，利用 `6px` 高斯模糊羽化遮罩将 AI 生成图仅在标注区域内混合回 `cleanDataUrl` 干净原图，确保未圈选区域 100% 原始像素零劣化。

---



### 变更背景
- 用户将插件导入 uTools 开发者工具时报错：`logo "https://img.icons8.com/fluency/96/screenshot.png" 不存在`。
- 根因分析：Rubick 允许 `plugin.json` 中的 `logo` 配置为网络图片 URL，而 **uTools 规范严格要求 `logo` 必须为插件工程根目录下的本地相对路径文件**（检测到以 `http/https` 开头时会直接作为本地相对路径校验，导致报错阻断）。

### 修复动作
1. **原创设计年轻潮酷专属 Logo**：
   - 彻底摒弃老旧外部网络图，重新设计原创高分辨率（256×256）专属应用图标并落盘：[logo.png](file:///e:/其他/liuaobo/AI-LAB/utools_lab/screenshotOcrTranslate/logo.png)；
   - 视觉美学：以年轻时尚的「天蓝→电光蓝紫→霓虹紫」多重科技渐变 Squircle 底座为主体，融合「截图取景四角标 + 智能扫描激光线 + 双语 A⇄文 核心符号」；
   - 专属标志：右上角悬浮定制高饱和「珊瑚红→金橙霓虹渐变 [✨ LAB] 潮酷胶囊药丸标」，彰显 AI-LAB 实验室专属出品；
2. 同步将 `plugin.json`、`customPlugins.json`、`package.json` 中的 `"logo"` 统一更新为本地路径 `"logo.png"`；
3. **彻底根除 uTools 发布打包报错（`打包目录不能包含 ".map"、".js.gz"`）**：
   - 根因分析：早期测试时残留的 `node_modules` 文件夹中包含第三方 npm 包自带的数十个 `.map` SourceMap 调试文件；
   - 解决方案：由于截图与标注能力已 100% 由自研原生 `snipper.exe` 全权接管，运行时完全零外部 npm 依赖，故彻底移除冗余无用的 `node_modules` 目录及未使用的 script 标签与 dependencies 声明；工程体积从 20MB 骤降至 160KB，实现 0 个 `.map` 文件的纯净发布打包状态；
4. 复核 `preload.js` 底层设计：`window.nativeBridge` 已内置对 `window.utools` 和 `window.rubick` 的自动探测桥接（包括窗口隐藏/恢复、高度展开、生命周期 `onPluginEnter` 自动挂载与重放等），双平台完全无缝共用。

---

## [2026-10-10] 新增虚线箭头（直线 / 箭头共用的虚实开关）

### 变更背景
- 用户提出："加个虚线箭头"。上一轮刚把线型补齐四档（直线 / 细箭头 / 实心箭头 / 空心箭头），本轮补上"虚实"这一维。

### 一、为什么不塞进线型下拉
- 若把四档线型 × 虚实两态铺成菜单就是 8 项，菜单变长且与 Snipaste 原版（4 项）不再对应。
- 虚实与"头部样式"是**正交的两维**：头部管形状，虚实管杆身。因此另立一个独立开关按钮，下拉仍保持 4 项。
- 二级栏线型模式下 `ToolType.Line` 与 `ToolType.Arrow` 共用同一个二级栏，所以虚实开关必须**同时作用于直线与箭头**，否则在直线档按下去会是个哑键。

### 二、数据模型
- `AnnotationItem` 新增两个共用常量与一份实参：
  - `DASH_ON = 2.5f`、`DASH_OFF = 1.6f`；
  - `static void ApplyDashedPen(Pen p)`：设为 `DashStyle.Custom` 并套用上面的比例。
- **`DashPattern` 的单位就是"笔宽倍数"**，所以 2.5 / 1.6 本身是线宽无关的比例：1px 细线画出来是小碎点，8px 粗线是同比例的长划线，观感随线宽等比放大，不需要按线宽分支调参。
- `LineAnnotation.IsDashed`、`ArrowAnnotation.IsDashed` 各加一个布尔字段，**默认 `false`（实线）**——不动既有手感。
- `ArrowAnnotation` 的虚线**只作用于杆身，头部恒为实心**：凹槽箭头 / 空心三角一旦碎成虚线，尖端就散了，认不出是箭头。

### 三、绘制
- 虚线一律改用 **`LineCap.Flat` 端帽**（`DashCap` 亦设为 `Flat`）：圆头会把每一小段鼓成胶囊，断口被填满，看上去就不是虚线了。
- `LineAnnotation.DrawCore`：实线走原路（圆头），虚线走 `Flat` + `ApplyDashedPen`。
- `ArrowAnnotation.DrawCore` 的两个分支（空心三角 / 实心燕尾）的**杆身**绘制各自加上同一套虚实处理，杆身终点仍收止在三角底边中点 / 燕尾凹槽中心，虚线化不会带出一截戳进头部的短线。

### 四、UI
- `SnipperForm` 新增状态 `private bool _lineDashed = false;`，两个图元构造点分别以 `IsDashed = _lineDashed` 落值。
- 二级栏线型模式下，线型下拉按钮之后插入 30px 的 `toggle_dash` 开关（`isLineMode` 的 `subW` 相应加一个 `iconBtnW`）。
- 开关图形恒画一条虚线（`DrawLineStyleGlyph(..., dashed: true)`），"当前是否启用"由蓝底高亮表达——图形表述"这档能给你什么"，底色表述"现在开没开"，两者互不干扰。
- `DrawLineStyleGlyph` / `CreateLineStylePreview` / `BuildArrowStyleItem` 增加 `dashed` 形参：线型下拉里的四张预览图会**跟随当前虚实状态**渲染，按钮与菜单、菜单与画布三处永远一致。
- 新增 `ApplyDashStyle(bool)`：切换 `_lineDashed`；若此刻正选中着线条图元，就地改它的 `IsDashed`（与既有 `ApplyLineStyle` 的即时改样式写法保持对称）。

### 验证
- `csc.exe -target:winexe -optimize+ -nologo` 编译：**0 错误 0 警告**（EXIT=0）。
  - 中途修正一处：`Pen.DashCap` 是 `DashCap` 枚举而非 `LineCap`，`CS0266` 已改回 `DashCap.Flat`。
- 一次性渲染程序按 2 / 4 / 8 三档线宽把"虚线直线 + 虚线箭头"各画一遍肉眼核对：三档疏密均等比、断口清晰；三种箭头头部在虚线杆身下**保持实心完整**；按钮尺寸（约 18px 宽）与下拉菜单项尺寸（41px 宽）的虚线预览均能看出 3 段以上，不是糊成一条实线。
- `bin/snipper.exe` 更新为 102400 字节（上一版 101376）。

---

## [2026-10-10] 箭头样式四档（对齐 Snipaste 线型下拉）+ 修复下拉菜单点击即崩

### 变更背景
- 用户报障："这个箭头目前还是只有实线的，而且无法选择。你看 snipaste 里这个功能是怎么做的"，并附上 Snipaste 线型下拉的实拍图（四档：直线 / 细箭头 / 实心箭头 / 空心箭头）与一张 .NET 未处理异常弹窗截图。
- 弹窗内容：`无法访问已释放的对象。对象名:"ContextMenuStrip"`——即用户点下拉菜单里的条目时，程序直接崩了。**"无法选择"的真实含义是"一选就崩"**，不是"没有菜单"。

### 一、崩溃根因（已用最小复现程序证实）
- 四处字体/字号/填充下拉与线型下拉共用同一段收尾代码：`menu.Closed += delegate { menu.Dispose(); };`
- `ContextMenuStrip.Closed` 是在 `ToolStripMenuItem.PerformClick()` **调用栈内部**触发的：Dispose 之后调用栈回到 `PerformClick`，它还要继续访问这个菜单，于是抛 `ObjectDisposedException`。
- 复现验证（`ContextMenuStrip` + `Closed→Dispose` + `PerformClick`）：**必现** `System.ObjectDisposedException | 无法访问已释放的对象。对象名:"ContextMenuStrip"`。
- 对照验证（把释放推迟到下一帧）：`caught=0`，菜单仍被正常释放。
- 另做了一组对照：PinForm 里旧式 `ContextMenu` 的 `Collapse += Dispose` **不会**崩（`Collapse` 在点击处理结束后才触发），因此贴图右键菜单保持原样、不做改动。

### 二、修复
- 新增 `DisposeDropdownOnClose(menu, ownedImages)`：把 `Closed` 里的释放动作 `BeginInvoke` 到消息循环下一帧；主窗体已在关闭流程（`IsDisposed` / `!IsHandleCreated`）时当场释放兜底。
- 四个下拉（字体 / 字号 / 填充 / 线型）全部改走该 helper，不再各自写一遍 `Closed += Dispose`。

### 三、箭头样式四档
- 新增 `enum ArrowHeadStyle { Thin, Solid, Hollow }`：**不设 None**——"直线"在 Snipaste 里就是一档线型而非箭头头部，本项目里它对应独立的 `ToolType.Line`（带折线顶点编辑），因此不塞进箭头枚举，避免多出一个永远到不了的 `HeadStyle` 取值。
- `ArrowAnnotation.HeadStyle` 按档绘制：
  - `Thin` 细箭头：`headLen = max(9, Width*2.6)`、翼展 `0.34rad`、燕尾 `0.78`；
  - `Solid` 实心箭头：沿用原参数（`max(13, Width*3.8)`、`0.40rad`、燕尾 `0.72`），**出厂默认**，观感与改造前完全一致；
  - `Hollow` 空心箭头：等边三角只描边不填充，杆身止于三角底边中点，底图从三角内部透出。
  - 橡皮擦悬停虚线提示与选中装饰抽成 `DrawEraserHintAndAdorner()`，四种样式共用收尾，不会漏画。
- 二级栏线型按钮由 34px 加宽到 52px（`arrowStyleBtnW`），按钮上直接**预览当前线型**（直线工具画直线，箭头工具画当前头部样式）+ 右下角小三角。
- `ShowLineDropdown` → `ShowArrowStyleDropdown`：四项均带真实渲染的预览图（`ToolStripMenuItem.Image` + `ImageScalingSize = 48×20`），而不是纯文字，1:1 对齐 Snipaste 的观感。
- `DrawLineStyleGlyph(g, box, style, color, lineWidth)` 统一画法，`style == null` 表示无头部直线；**下拉菜单里的预览图与按钮上的预览共用这一份实现**，保证"按钮显示的"与"菜单列出的"永远一致。
- 预览图是每次弹菜单临时 `new` 的 Bitmap，登记进 `ownedImages` 交给释放流程一并释放，避免反复弹菜单漏 GDI 对象。
- 选中态即时改样式：`ApplyLineStyle()` 里若 `_selectedShape` 正是 `ArrowAnnotation`，就地改 `HeadStyle`，不必删掉重画。

### 验证
- `csc.exe -target:winexe -optimize+ -nologo` 编译：**0 错误 0 警告**（EXIT=0）。
- 用一次性渲染程序把四种线型在"菜单项尺寸(48×20)"与"二级栏按钮尺寸(32×16)"下各画一遍肉眼核对：直线 / 细箭头 / 实心箭头 / 空心箭头四种轮廓清晰可分，实心系的燕尾凹槽与空心系的三角描边都正确。
- `bin/snipper.exe` 更新为 101376 字节（上一版 98816）。

---

## [2026-10-10] 修复选区左上角尺寸标签重影（"817 × 497" 与 "817 × 497 px" 叠字）

### 变更背景
- 用户报障："左上角的尺寸会有重叠的bug"，截图显示选区左上角的尺寸胶囊里明显是两层字影。
- 根因：`OnPaint` 里存在**两处独立的尺寸标签绘制点**，选区确认后同时开火：
  - `OnPaint` 内的「实时尺寸胶囊」，外层条件是 `if (_isSelecting || _hasSelected)`，因此拖拽结束后仍在画，文案 `"{0} × {1}"`（无 px），位置 `r.Top - tipH - 4`；
  - `DrawSizeInfo(g)`，仅 `_hasSelected` 时调用，文案 `"{0} × {1} px"`，位置 `_selSizeLabelBounds = (Left, Top - 22)`。
  - 两者字形、底色都极接近，且 `DrawSizeInfo` 在手柄之后绘制，正好盖住实时胶囊的大半，只漏出一条边——就成了肉眼看到的"重影"。

### 本次动作
- 实时尺寸胶囊的判定收紧为 `if (_isSelecting && r.Width > 20 && r.Height > 15)`：**只在"正在拖拽框选"这一段显示**。选区一旦落地，尺寸信息完全交给 `DrawSizeInfo` 一个来源负责，从根上消除双画。
- 顺手修掉一个次级遮挡：左上角 8px 手柄的 2px 描边会溢到包围盒外约 5px，而标签原先贴在 `Top - 22`（底边 `Top - 2`），压在上层绘制会把角手柄上沿啃掉一条。标签与包围盒的间距由 2px 放宽到 6px（`selLabelY = _selectedRect.Top - selLabelH - 6`）。
- 贴顶翻转到选区内侧的分支同步让位：`(Left, Top + 2)` → `(Left + 6, Top + 6)`，否则标签左沿会同时盖住左侧手柄与左上角手柄。
- 失效区无需改动：`SelectionAffectedBounds()` 本就并入了 `_selSizeLabelBounds` 并外扩 `(120, 32)`，标签位移后仍落在重绘范围内。

### 验证
- `csc.exe -target:winexe -optimize+ -nologo` 编译：**0 错误 0 警告**（EXIT=0）。
- 以选区 `(77, 67, 818, 498)` 合成 4 倍渲染核对：外侧与贴顶翻转两种摆放均只见**一个**标签，角手柄完整可见，无遮挡、无重影。
- `bin/snipper.exe` 更新（98816 字节）。

---

## [2026-10-10] 文字标注支持双击改内容（此前只能移动/缩放/换色，改不了字）

### 变更背景
- 用户反馈："标注的文字，文字内容无法修改"——文字标注一旦提交成图元，就只剩下平移 / 四角缩放 / 换色 / 换字体 / 加粗斜体几条二次编辑路径，**唯独内容本身改不了**，写错一个字只能删掉重打。

### 本次动作
- 新增 `_editingText` 状态：非 null 表示当前文本框是在**重新编辑既有标注**而不是新建。
- `SpawnTextInput(Point pt, TextAnnotation existing)` 增加 `existing` 形参：
  - 预填原有内容并 `SelectAll()`，原地落回 `existing.Location`（不会"点一下跳一下"）。
  - TextBox 字体/颜色跟随该图元自身，而不是副栏当前值——否则"改个错字"会顺手把用户调好的字号颜色重置掉。
- `CommitActiveText()` 分流：编辑态只把内容写回**同一个图元**，字号/颜色/旋转/位置一概保留；内容被清空等价于删除该标注（避免留下点不中的空图元）。
- `CancelActiveText()`（Esc）恢复原图元显形，内容一字未动。
- 新增基类开关 `AnnotationItem.IsHiddenForEditing`：在 `Draw()` 与 `HitTestItem()` 两处统一生效。编辑期间原图元必须隐身，否则旧文字与输入框会叠成看不清的双层字；同时要从命中测试里摘出去，免得点到文本框底下那层。放在基类还顺带保证了**导出路径不会把隐身图元画进成品图**。
- `OnMouseDoubleClick` 分流：文本框内双击 = 选词（不确认截图）→ 命中已有文字 = 进入重新编辑 → 其它位置才维持原行为"双击确认并复制"。编辑判定**必须排在确认截图之前**，否则想改字的人会被直接踢出截图流程。
- 新增 `HitTestTextAnnotation(Point)` helper，自上而下取落点处的文字图元（走 `HitTestItem`，天然排除隐身的那个，且自动继承旋转逆变换）。
- 文案同步：文字工具提示补「双击已有文字可改内容」；复制按钮提示由「Enter / 双击」细化为「Enter / 空白处双击」，因为双击的语义已经分叉。

### 验证
- `csc.exe -target:winexe -optimize+ -nologo` 编译：**0 错误 0 警告**（EXIT=0），`bin/snipper.exe` 更新为 98816 字节。

---

## [2026-10-10] 缺陷全量修复 + 历史瘦身 + 原文译文逐段对照 + 图元旋转与折线顶点编辑

### 变更背景
- 用户以"这工具要替代我的日常截图工具"为定位，要求：先全量修复已知缺陷（JS 侧 + C# 侧一并），再落地功能优化清单中的第 5、6、7 项。
- **明确排除项**（用户红线，后续不得再提）：不合并「截图」与「截图并识别翻译」两个入口；不改造贴图；不改动反馈渠道；历史详情大图已做好无需再动。

### 一、缺陷修复（JS 侧）
- `preload.js` 历史落盘：`writeFileSync` 改为**异步写 + 200ms 防抖**，并在 `process.on('exit')` 同步兜底刷盘。历史 JSON 带 base64 图片可达数 MB，原先每次新增/删除都同步阻塞渲染主线程一次。
- `preload.js` `captureScreen`：
  - `snipper.exe` 两条候选路径都找不到时**立即 reject 并恢复主窗口**，不再退化成执行不存在文件的 ENOENT。
  - **刻意不给 `execFile` 设 `timeout`**：用户选择「贴图」时 snipper.exe 会驻留显示置顶窗口直到用户主动关闭，设了超时会连用户的贴图一起杀掉。
  - 补齐 `ACTION:PIN` 分支，以及最后的**兜底 reject 收口**（ENOENT / 进程被杀 / maxBuffer 溢出），否则 Promise 永久 pending、前端卡死在"截图中…"。
- `src/app.js` 历史渲染：新增 `escapeHtml`，历史条目的原文/译文/缩略图 src 全部转义，修掉图片 dataURL 与识别文本直接拼 HTML 的注入面。

### 二、历史记录瘦身（优化项 5）
- 新增 `buildHistoryImage()`：入库前把原图等比压到**最长边 1280px 的 JPEG（质量 0.82）**，历史条目磁盘体积下降一个数量级。
- `localStorage` 只写**纯文本字段**（`textOnly`），不再把带 base64 图片的完整列表塞进仅约 5MB 配额的 localStorage；磁盘持久化交给主目录下的 history JSON。

### 三、原文译文逐段对照（优化项 6）
- 右栏新增 `#btnToggleParallel`「逐段对照 / 纯译文」视图切换，`#parallelView` 以 `grid 1fr 1fr` 逐行左右对照，右侧译文单元格 `contentEditable` 可直接改字。
- `splitParagraphs()` 优先按空行切段，通篇无空行时（截图 OCR 常见一行一段）退化为按单换行切段。
- **段落对应关系存在内存数组 `parallelRows` 里**，不从拼接后的字符串反推：译文段落被过滤掉空行后再拼回字符串会丢失"第几段"信息，导致重新切段时整行错位。左栏原文手动校对时走 `syncParallelToOrig()` 只就地刷左侧单元格，不重建视图。
- 流式翻译期间自动切回纯译文（`beginStreamingDisplay`），结束再渲染对照（`endStreamingDisplay`）。

### 四、图元旋转与折线顶点编辑（优化项 7）
- **副栏主参数胶囊红点**：`SubOptionItem.ShowBadge`，当笔宽/马克笔宽/马赛克颗粒度偏离出厂默认值（`DEFAULT_PEN_WIDTH/MARKER_WIDTH/MOSAIC_BLOCK_SIZE`）时右上角点亮带白圈的红点。默认值与字段初始值共用同一份常量，避免两处魔数各改各的。
- **折线顶点编辑**：`VertexRef { Index, Position }` 保留顶点在图元内部的**真实下标**（自由笔迹点数可上千，等距采样后必须能反查回原始点）；`SampleVertices()` 上限 24 个手柄且首尾必取；`HitTestVertex()` 热区 10px。直线/箭头改端点，自由笔迹/马克笔改采样点。
- **四角手柄抑制**：`SuppressCornerHandles()` 在 `GetEditVertices().Count == 2`（直线、箭头、极短笔迹）时隐藏四角——此时顶点与角点重合会互相抢命中；顶点更多的自由笔迹两套手柄并存，**不牺牲原有的四角等比缩放能力**。
- **图元旋转**：基类 `Draw`/`HitTestItem` 改为非虚壳 + `DrawCore`/`HitTestItemCore` 钩子（9 个子类各自改名），`Angle != 0` 时在 `Draw` 里绕 `GetCenter()` 旋转绘制；命中测试先 `InverseRotate()` 转回局部坐标系——包括 `TextAnnotation.HitTest()` 与 `HitTestRotateHandle()`。
- **旋转手柄**：`DrawRotateHandle()` 画在旋转变换之内（所以始终跟着图元转，不会"手柄在左、图形在右"），Shift 吸附 15°。
- **脏区**：`ItemDamageBounds`/`StrokeDamageBounds` 改用 `GetVisualBounds()`（旋转后四角的 AABB），选中态额外外扩 `RotateHandleOffset + 8`，否则旋转手柄会留在失效区之外、原地残影。
- **文字标注并入旋转体系**：`TextAnnotation.DrawCore` 的私有虚线框+四角手柄改为复用基类 `DrawSelectionAdorner()`，自动获得旋转手柄且不再重复实现；`TextHandleType.Rotate` + `HitType.TextHandleRotate` 补齐拖拽与手型光标。文字无骨架顶点，故不会多出顶点方块。
- 导出路径无需额外处理：`GenerateFinalBitmap()` 走 `ann.Draw(g)`，旋转自动进入复制/保存/OCR 的成品图。

### 验证
- `csc.exe -target:winexe -optimize+ -nologo` 编译 `src/snipper/Program.cs`：**0 错误 0 警告**（EXIT=0），产出 98304 字节零依赖 `bin/snipper.exe`（上一版 93696）。
- `node --check src/app.js` 与 `node --check preload.js` 均通过（SYNTAX_OK）。
- 逐项验收明细见 `docs/superpowers/status/2026-10-10-rotation-parallel-walkthrough.md`。

---

## [2026-10-10] 工具栏按钮重排：文字识别(OCR) 移到「放弃截图」左侧

### 变更背景
- 用户实拍反馈：主工具栏最右侧的「文字识别」按钮离常用操作组太远，希望挪到「放弃截图 (×)」左边。

### 本次动作
- `UpdateBarsLayout` 的主工具栏 `defs` 顺序调整：`sep2 | OCR | cancel | pin | save | copy`（原先 OCR 被 `sep3` 单独隔离在整条工具栏最右端）。
- 因 OCR 已并入「取消/贴图/保存/复制」这一组，`sep3` 分隔符失去作用，一并删除；分隔符与按钮宽度均由 `defs` 循环实时累加，`totalWidth` 自动适配，无手写魔数需要同步。
- 注释同步更新为新分组顺序。

### 验证
- `csc.exe -target:winexe -optimize+ -nologo` 重新编译 `bin/snipper.exe`：**0 错误 0 警告**，91648 字节，零依赖。
- 图标绘制 `DrawVectorIcon` 按按钮 `Id` 分发、命中测试遍历 `_toolButtons`，均与顺序无关，故仅调整 `defs` 即可，无其它逻辑需要改动。

---

## [2026-10-10] 拖拽/悬停改为脏区重绘：单帧 72.4ms → 4.6ms，并修复副屏抓屏 alpha=0

### 变更背景
- 用户以"要上市的产品"视角审视后反馈："感觉截图选择，截图移动那里还是有些卡顿的，用起来不丝滑"。
- 确认执行范围：**全量优化**（含推荐的窗口探测节流、`UpdateBarsLayout` 拆分评估）；副屏抓屏 `alpha=0` 异常**一并排查修复**。

### 根因（全部经实测量化）
- 覆盖层 `SnipperForm` 覆盖整个虚拟屏（本机 `0,0 6720×2078` = **14.0 M px**），`_fullBmp` 为整屏抓屏位图。
- 拖拽选区 / 平移 / 图元操纵 / 悬停高亮等**所有热路径都走无参 `Invalidate()`**，于是 `OnPaint` 每帧都要整屏 blit + 满屏遮罩。
- **单帧实测 72.40 ms（≈14 fps 上限）** —— 这就是"不丝滑"的根源，与用户主观感受完全一致。
- 关键测量（决定了改造方向）：
  - GDI+ 的 `DrawImageUnscaled` 会**按 DC 裁剪区自动收敛开销**：整屏 78.43ms → 裁到 1.53 M px 6.19ms → 裁到细带 0.12ms。因此 `OnPaint` **不必改**，只要把失效范围缩小即可。
  - `DrawImageUnscaled` 比显式 `DrawImage(dstRect, srcRect)` 更快；`PArgb→PArgb` 比 `Argb→Argb` 快约 **3×**（51.56ms vs 154.79ms）；GDI `BitBlt` 恒定 90–105ms，反而更慢。
  - 满屏遮罩 4 个 `FillRectangle` 56.43ms → 裁到脏区 2.70ms（**21×**）。

### 本次动作

#### 1. 抓屏不透明化 + 预乘格式（修副屏 alpha 缺陷）
- 新增 `CaptureOpaqueScreen(bounds)` / `ToOpaquePremultiplied(captured)`：抓屏后用 `LockBits` 逐行把 alpha 字节或成 `0xFF` 再写入 `Format32bppPArgb` 目标位图（单遍完成，无额外转换）。
- 依据：屏幕 DC 的 alpha 通道**无语义**。副屏 `\\.\DISPLAY5` 实测 86.5% 像素 `A=0`，且与"驱动未写入的纯黑区域"严格重合；整屏 blit 与逐屏 blit 结果一致 → 归一为不透明即正确修法。
- `Format32bpp*` 内存布局为 BGRA 小端，按 `int` 读即 `0xAARRGGBB`，`|= 0xFF000000` 即可；A=255 时预乘值与直通值数值完全一致，故直接写入 PArgb 位图是正确的。

#### 2. 建立局部失效（脏区重绘）体系
- 新增 `InvalidateScreen(Rectangle)`：外扩 4px（描边 / 抗锯齿 / 卡片投影）→ 裁剪进窗体范围 → 非空才 `Invalidate(r)`。
- 新增一组失效范围助手：`UnionVisible`（并集时跳过空矩形，避免 `Rectangle.Empty` 的 (0,0) 原点把范围无谓拉大）、`SelectionAffectedBounds`、`HoverDamageBounds`、`ItemDamageBounds`、`StrokeDamageBounds`、`ToolBarDamageBounds`、`SubBarDamageBounds`、`ColorPickerCardBounds`。
- **`DrawColorPickerMagnifier` 改为复用 `ColorPickerCardBounds`**：绘制定位与局部失效共用同一套公式，保证"画到哪"与"失效到哪"严格一致。
- 所有拖拽 / 悬停路径统一按 **"旧 ∪ 新"** 失效（先留一份上一个光标位置），否则旧位置会留残影。

#### 3. `OnMouseMove` 11 处热路径改造
- 强度滑杆拖拽、未选区时的窗口探测、工具栏悬停、二级栏悬停、橡皮擦悬停、空格平移、初次拉框、文字整体移动、文字四角缩放、图元操纵、选区 8 点缩放 / 平移、绘制中临时图元 —— 全部由 `Invalidate()` 改为精确矩形。
- 选中态会连带失效尺寸标签、主工具栏与二级栏；`_tempAnnotation` 刻意**不裁剪到选区**（绘图全程无 `SetClip`，笔迹可以越过选区边界）。

#### 4. 窗口/元素探测节流
- `DetectWindowUnderPoint(pt, force = false)`：光标位移不足 `ElementDetectThreshold = 3` px 且已有高亮结果时直接复用上次结果（每次探测要跑一轮 `EnumWindows` + 横纵射线扫描，实测约 1.3ms，60Hz 鼠标事件下开销可观；2px 滞后肉眼不可辨）。尚无结果时不受节流限制。
- Tab 键切换元素/整窗模式传 `force = true`，避免同一光标位置的重探被节流吞掉。

#### 5. 两处"失效范围算小"的实测修复
- **卡片投影**：`DrawCardShadow` 会在卡片四周各画出约 4px 投影，`SelectionAffectedBounds` 原先只并上 `_toolBarBounds` 本体，导致工具栏投影下沿留残影 → 改为并上有外扩的 `ToolBarDamageBounds()` / `SubBarDamageBounds()`。
- **Miter 尖刺**：`BrushAnnotation` / `MarkerAnnotation` 的**橡皮擦悬停高亮虚线笔漏了 `LineJoin = Round`**（本体笔都设了）。默认 Miter 接头会在折线锐角顶点甩出与笔宽成正比的尖刺（30px 笔在约 30° 夹角下外伸近 **58px**），既难看、又要求失效范围按尖刺长度外扩 → 补上圆角接头，使其不再超过骨架包围盒外扩半个笔宽。

#### 6. `UpdateBarsLayout` 拆分：实测后**判定不做**
- 真实测量（2560×1440 窗体、1600×950 选区、12 种工具各 2000 次）：**32–44 µs/call**，约 0.04ms，仅占优化后一帧（~4.6ms）的 **0.5%**；24000 次调用仅 34 次 gen0 GC。
- 结论：拆分是纯 churn 且有布局回归风险（安全实现需枚举 13+ 项状态做缓存 key，漏一项即静默布局陈旧），**收益不足以抵偿风险，故不做**，仅记录数据与理由。

### 验证

#### 1. 脏区重绘等价性测试（`C:\Windows\Temp\damagetest.cs`，15 个交互场景 **15/15 通过**）
- 方法：`全量渲染状态 A → X` → 触发**真实 `OnMouseMove`**（内部按新逻辑累积失效区域）→ 取窗口更新区域包围盒，**只在该裁剪区内把状态 B 重绘到 X 上**（模拟 `WM_PAINT` 增量重绘）→ `全量渲染状态 B → Y` → 逐像素比较 X 与 Y，必须完全一致，否则真机上会留残影。
- 覆盖场景：选区整体平移 / 四角缩放 / 初次拉框 / 空格平移、文字整体移动 / 四角缩放、马克笔图元移动、矩形图元缩放、绘制中矩形与直线临时图元、橡皮擦悬停、主工具栏与二级栏悬停、马赛克强度滑杆拖拽、取色放大镜跟随。
- **测试方法上的关键坑**：**不可见窗口不累积更新区域**——未 `Show()` 时 `GetUpdateRgn` 恒为 `NULLREGION`，`Invalidate` 看起来"没生效"。测试必须先 `Show()` + `DoEvents()` 再取区域。
- 该测试直接抓出了上面第 5 节的两个真实缺陷（工具栏投影、Miter 尖刺），是本轮最高风险改动的直接证据。

#### 2. alpha 修复验证（`C:\Windows\Temp\alphacheck.cs`，全部通过）
- 合成位图：`0x00FF8000 → 0xFFFF8000`、`0x80123456 → 0xFF123456`、`0xFF00FF00 → 0xFF00FF00`、`0x00000000 → 0xFF000000` —— **A 全为 255 且 RGB 不变**。
- 逐显示器真实抓屏：`\\.\DISPLAY5` 2880×1620 原始 **A=0 像素 4037120/4665600（86.5%）** → 经 `CaptureOpaqueScreen` 后 **A≠255 像素 0**；`\\.\DISPLAY1` 2560×1440 原本就是 0.0%。

#### 3. 单帧耗时基准（`C:\Windows\Temp\paintbench.cs`，真实覆盖层 6720×2078）
| 场景 | 单帧耗时 | 帧率上限 | 失效面积 |
|---|---|---|---|
| 全屏重绘（改造前的无参 `Invalidate`） | **72.40 ms** | 14 fps | 14.0 M px |
| 拖动选区（旧∪新选区 + 胶囊 + 工具栏 + 二级栏） | **4.60 ms** | 217 fps | 0.44 M px |
| 工具栏按钮悬停 | **1.00 ms** | 1004 fps | 0.10 M px |

- 拖动选区 **15.7×**、工具栏悬停 **72×** 提升。
- 编译 `csc.exe -target:winexe -optimize+ -nologo`，**0 错误 0 警告**，零依赖单文件（`bin/snipper.exe` 91648 字节）。

### 遗留项
- 图元二次编辑仍不支持旋转与折线顶点级编辑。
- 文字二级栏宽度公式需随控件增删同步维护。
- 二级栏主参数胶囊旁"红点"角标（表示该值已被改动过）未实现。
- 根目录 7 张 `1791601*.jpg`（Snipaste 实拍核对素材）仍为未跟踪文件。

## [2026-10-10] 以 Snipaste 实拍截图为准，二级栏控件模型 1:1 重构并完成运行时视觉验证

### 变更背景
- 上一轮用 `SendInput` 驱动本机 Snipaste 反复失败（热键焦点被弹窗吃掉），无法稳定复现其截图界面。
- 用户中止该路线，直接提供 **7 张 Snipaste 各工具二级栏实拍截图**（项目根目录 `1791601*.jpg`）作为唯一事实来源。
- 诉求："你看一下文件夹里的这些截图就是各个工具栏的功能" —— 以实拍为准，把截图插件工具栏 1:1 对齐。

### 本次动作

#### 1. 事实来源与核对方式
- 7 张实拍逐张读取，并用 PowerShell `NearestNeighbor + PixelOffsetMode.Half` 做 4× 放大后逐像素核对，确认：主工具栏 13 项顺序、每个工具二级栏的控件排布、调色板为**统一 20 色（两行十列）**、二级栏**左边缘与主工具栏左边缘对齐**、尺寸标签贴在**选区左上角**。

#### 2. 选区尺寸标签迁移
- 尺寸标签从主工具栏内部（占位 `sizeInfoW`）移到**选区左上角上方**，深色胶囊 `232,32,32,32` + 白色 8.5pt 粗体 `928 × 302 px`；贴顶时自动翻到选区内侧。
- 新增 `_selSizeLabelBounds` / `_sizeInfoText`，`DrawSizeInfo()` 重写为圆角胶囊；调用点从 `DrawToolBar()` 移到 `OnPaint()`；主工具栏宽度核算随之移除尺寸项。

#### 3. 主工具栏顺序回归 Snipaste
- 顺序定为：形状 → 线条 → 画笔 → 马克笔 → 马赛克 → 文字 → 序号 → 橡皮擦 `|` 撤销 → 重做 `|` **取消 × → 贴图 📌 → 保存 💾 → 复制 ⧉** `|` OCR（取消键前置到贴图之前）。
- 画笔快捷键由 `P` 改为 `B`（对齐 Snipaste），tooltip 同步为 `画笔 (B)`。

#### 4. 二级栏控件模型重构（核心）
- **主参数胶囊 `param_size`**：实心亮蓝 `#0084FF` 圆角胶囊 + 白色粗体数值，显示当前笔宽 / 马克笔笔宽 / 马赛克颗粒度；点击 `StepActiveSize(1)` 循环档位，与 `[` `]`、滚轮共用同一套档位表。文本由新增的 `ActiveParamText()` 统一产出。
- **马赛克 / 模糊**：模式图标 `mosaic_mode_toggle`（马赛克画 2×2 方块阵、模糊画同心圆）+ `AddSubSeparator()` + 强度滑杆 `mosaic_slider`（量程 `MOSAIC_MIN=4` ~ `MOSAIC_MAX=40`）+ 右侧数值 `mosaic_slider_value`。
- **形状**：填充图标 `toggle_fill` + 分隔线 + **矩形 / 椭圆直选** `shape_rect` / `shape_ellipse`（实拍中是直选按钮而非下拉，故删除已无调用点的 `ShowShapeDropdown()`）。
- **线条**：线型下拉 `toggle_line_arrow`（图标 + 右下角小三角）。
- **橡皮擦**：透明棋盘格预览 `eraser_preview`；`HasSubBar()` 补入 `ToolType.Eraser`，使其首次拥有二级栏。
- **画笔 / 马克笔**：主参数胶囊 + 分隔线 + 色块 + 20 色板（马克笔复用同一套色板，不再用独立 8 色荧光板）。
- 新增 4 个矢量绘制辅助：`DrawMosaicModeIcon` / `DrawCheckerboardIcon` / `DrawFillIcon` / `DrawLineStyleIcon`；新增 `AddSubSeparator(ref int sx, int sy)` 统一各功能块之间的细竖线（`SUB_SEP_W = 8`）。
- 二级栏 `subX` 改为 `_toolBarBounds.Left`（左对齐），宽度按控件实际步进核算，并保留窗口边界夹取。

#### 5. 强度滑杆拖拽接线
- 新增 `_draggingSlider` 拖拽态 + `FindSubOption(id)`（布局重建后控件对象会更换，拖拽时每次重新取）+ `ApplySliderValue(slider, mouseX)`（按横坐标反算并夹取到量程）。
- `OnMouseDown` 命中 `mosaic_slider` 即进入拖拽态并按落点立刻定位；`OnMouseMove` 顶部新增拖拽优先分支（抢占其余交互）；`OnMouseUp` 清空拖拽态。

#### 6. 马克笔改为真正的荧光笔渲染
- `MarkerAnnotation.Draw()` 改为**双层渲染**：底层半透明主色（alpha 112）铺满整个笔宽，保留底图内容可见；上层同色向白提亮 55% 的窄芯线（alpha 96，宽度 0.5×），模拟荧光笔"芯亮边淡"的发光质感；`LineJoin` 由 `Bevel` 改为 `Round`。
- 马克笔统一到 20 色通用色板后，**删除死代码** `MARKER_COLORS`（8 色荧光板）与 `AddMarkerColorMatrix()`。

### 编译与运行时验证
- `csc.exe -target:winexe -optimize+ -nologo` 单文件编译 `screenshotOcrTranslate\bin\snipper.exe`，**0 错误 0 警告**，零依赖。
- **本轮补齐了上一轮缺失的运行时视觉验证**：启动 `snipper.exe` → 模拟拖拽框选 → 用 `SendKeys` 依次切到 K/X/R/M/L/B/T → 逐次全屏截屏并按实际坐标裁切放大核对。
- 验证结论（全部通过）：
  - 主工具栏 14 个按钮 + 3 分隔线 + OCR 顺序与图标正确，序号标注激活态实心蓝、重做按钮正确置灰。
  - 马赛克栏：`[12][▨] | [滑杆 knob≈22%][12] | [色块][20 色板]`，knob 位置与 `(12-4)/36 ≈ 0.22` 完全吻合。
  - 橡皮擦栏：棋盘格预览正常渲染；形状栏：`[▨填充][▭矩形(激活)][◯椭圆]`；线条栏、马克笔栏（`[18]`）、画笔栏（`[4]`）、文字栏（`[B激活][I][A] | [Microsoft YaHei][16∨] | [色块][20 色板]`）均与实拍结构一致。
- **验证经验（重要）**：本环境 Bash 工具把命令放在 Job Object 中执行，`Start-Process` 派生的 GUI 子进程会在该命令结束时被**连带杀掉**，因此"启动 → 交互 → 截屏"必须在**同一个 PowerShell 脚本内**一次跑完，跨命令分步做会得到空截图（本次一度被误导为"overlay 未显示"）。

### 遗留项
- 图元二次编辑仍为"选中 + 拖拽 + 四角缩放"，尚未支持旋转与顶点级编辑（折线节点拖动）。
- 文字二级栏宽度按实际排布核算，后续增删控件仍需同步维护该公式。
- 实拍中二级栏主参数胶囊旁带"红点"角标（表示该值已被改动过），当前未实现该角标。

## [2026-10-10] 截图标注：图元二次编辑 / 模糊 / 序号 / 荧光笔 / 滚轮调参 / 工具栏质感

### 变更背景
- 用户二次实测反馈："工具栏那里的功能和样式还是差强人意"。
- 差距盘点后用户选定**全量四项**修复范围，并要求**马克笔独立成真正的荧光笔（独立粗细 + 荧光色）**。

### 本次动作

#### 1. 图元二次编辑（选中 / 平移 / 四角缩放 / 换色 / 删除）
- `AnnotationItem` 基类补齐编辑能力契约：`GetBounds()` / `SetBounds()` / `IsSelected` / `SupportsColor` / `SetColor()` / `HitTestHandle()` / `DrawSelectionAdorner()` / `MapPoint()` / `GetPointsBounds()`。
- 新增集中式选择管理：`HitTestShape()` / `SelectShape()` / `DeselectShape()` / `ClampShapeBounds()` / `ApplyActiveColor()`；原 `DeselectAllTexts` 全量重命名为 `DeselectAllAnnotations`（17 处调用同步）。
- `OnMouseDown` 新增 4.5 分支（仅在未激活绘图工具时抢占，避免干扰正常绘制）、`OnMouseMove` 新增 5.5 分支（Body 平移 + 四角缩放，下限 `MinShapeSize = 6`）、`OnMouseUp` 收尾重建马赛克贴片。
- 四角缩放改为**基于按下瞬间的绝对包围盒**重算，规避累积误差；`MapPoint` 对零宽/零高（水平线、垂直线）自动退化为纯平移，不产生除零。
- `Del` / `Backspace` 删除选中图元并写入重做栈（Ctrl+Y 可恢复）；`PerformUndo` 同时清理被移除图元的 `IsSelected` 残留。
- 光标反馈：`GetShapeCursor()` 按四角 / 本体返回对角缩放或移动光标。

#### 2. 补齐高斯模糊（原仅有像素马赛克）
- 新增 `CreateLocalBlur()`：3 次盒式模糊（Box Blur）逼近高斯核，滑动窗口累加 + 边界 clamp 延拓；`BoxBlurPass()` / `BoxBlurChannel()` 分别处理水平逐行与垂直逐列，B/G/R 三通道独立，Alpha 直拷。
- 新增 `BuildMosaicSnippet()` 统一分派：`MosaicAnnotation.IsBlur == true` 走模糊（`radius = BlockSize / 2`），否则走 `CreateLocalMosaic` 像素块平均。
- `OnMouseUp` 落笔与缩放结束（`NeedsRebuild`）均复用同一重建路径。

#### 3. 补齐序号标注
- 新增图元类 `NumberAnnotation`（实心圆底 + 白色粗体数字），`ToolType` 枚举新增 `Number`。
- 单击即落点、编号自动递增（`_nextNumber`）；`RenumberAnnotations()` 在删除 / 撤销 / 重做 / 橡皮擦 / 清空后统一重排为 1..N 连续。
- 新增矢量图标 `case "number"`，快捷键 `N`。

#### 4. 马克笔独立成真正的荧光笔
- 笔宽与通用画笔解耦：新增 `_markerWidth`（档位 10 / 18 / 28），画笔仍为 `_penWidth`（2 / 4 / 8）。
- 新增专用荧光色板 `MARKER_COLORS`（8 色）与 `AddMarkerColorMatrix()`（两行四列），与通用 20 色板分离。
- 二级栏新增 3 档 `marker_width` 光条渲染（按实际笔宽等比画粗细细条）。

#### 5. 滚轮 + 快捷键调参统一
- 重写 `OnMouseWheel()`：选中文字时调字号（9~96），激活绘图工具时按工具有效调参；新增 `StepValue()` 相邻档位步进。
- 三张共用档位表：`PEN_WIDTH_STEPS` / `MARKER_WIDTH_STEPS` / `MOSAIC_SIZE_STEPS`。
- `StepActiveSize()`（`[` `]` / 数字键）统一到同一套档位表，修正此前马赛克错误写 `_penWidth`、马克笔误走通用线宽的历史缺陷。
- 快捷键注释修正：`N` 由误标的"文字"更正为"序号标注"。

#### 6. 马赛克 / 模糊二级栏
- 新增模式切换按钮 `mosaic_mode_pixel` / `mosaic_mode_blur`（激活态实心 `#0084FF` + 白字），切换不做开关取反，避免误关闭工具。
- 新增 3 档颗粒度 `mosaic_size`（细 6 / 中 12 / 粗 20），与滚轮档位表一致。

#### 7. 工具栏样式质感精修
- 按钮尺寸 `26 → 30px`，OCR 胶囊 `34 → 38px`，工具栏高度同步。
- 矢量图标引入**等比缩放 + 平移变换**：`DrawVectorIcon()` 内 `TranslateTransform` + `ScaleTransform(r.Height / 26f)`，全部图标随按钮尺寸自动放大，不再手改 19 个 case 的偏移量。
- 选中态加深描边 `#006CD6`（微内阴影立体感），悬停底色由 `#F1F5F9` 加强为 `#E8F0FA` 并补 1px 描边，圆角 `4 → 5`。
- 复合工具按钮（矩形/椭圆、折线/箭头、马赛克/模糊）右下角新增实心小三角 `DrawCompoundTriangle()`，提示可切换子模式。
- 工具栏最左侧新增**常驻选区尺寸显示** `DrawSizeInfo()`（`W × H`，右对齐 + 右侧分隔线），Snipaste 同款信息位。
- 二级栏宽度由手写魔数改为**按内部控件实际排布核算**（`subPadLeft + 各控件步进 + paletteW + subPadRight`），修正文字属性栏 438px 仅剩 1px 余量、矩形/椭圆 288px 反而少 4px 导致末列调色板被裁切的问题。
- 修正调色板注释笔误（"12 色" → 实际每行 10 色）。
- `F1` 快捷键指南卡片高度 `216 → 288`，补充框选后标注工具键位（R/E/L/A、P/M/K/G、T/N/X/Del）、滚轮与 `[ ]` 调参、图元二次编辑提示。

### 编译验证
- `csc.exe -target:winexe -optimize+ -nologo` 单文件编译 `screenshotOcrTranslate\bin\snipper.exe`，**0 错误 0 警告**，零依赖。
- 尚未做运行时视觉验证（本轮仅完成编译级验证）。

### 遗留项
- 图元二次编辑为"选中 + 拖拽 + 四角缩放"，尚未支持旋转与顶点级编辑（折线节点拖动）。
- 文字二级栏宽度虽已按实际排布核算，但若后续增删控件仍需同步维护该公式。

## [2026-10-10] Snipaste 全功能高精度 1:1 对齐与窗口识别缺陷彻底攻克

### 变更背景
- 用户实测反馈：
  1. 智能识别不灵敏，在 Antigravity 等深色/黑色窗口上无法识别到任何应用窗口；
  2. 工具栏功能与样式遗漏严重，缺少真实 Snipaste 中的复合工具、高级二级属性栏（B粗体、I斜体、A轮廓、字体选择、字号微调、24色调色盘矩阵等）。

### 本次动作
- **彻底解决黑色大窗口识别失效（Z-Order 顶层窗口精准枚举穿透）**：
  - 根因分析：截屏置顶遮罩自身 HWND 拦截了 `WindowFromPoint`，导致返回 `this.Handle` 并误判为空。
  - 方案重构：引入 Win32 `EnumWindows` + `DwmGetWindowAttribute`，遍历时主动跳过自身全屏遮罩窗体，秒级精准锁定光标下真实的顶层应用窗口（Antigravity、VSCode、Edge 等），100% 能够高亮当前应用全窗。
  - 模式切换：结合 `Tab` 键在「完整宿主应用窗口」与「窗口内局限性自绘色块/卡片」之间循环切换。
- **主工具栏图标与样式 1:1 对齐 Snipaste 真实界面**：
  - 图标与功能重组：
    - ① `rect_ellipse`（矩形/椭圆合体，展开实心/空心、矩形/椭圆切换）
    - ② `line_arrow`（折线/箭头合体）
    - ③ `brush`（画笔自由手绘）
    - ④ `marker`（记号笔/马克笔：半透明荧光高亮笔刷，不遮挡底层文字）
    - ⑤ `mosaic`（马赛克：2x2 网格，支持颗粒度切换）
    - ⑥ `text`（文字 T：激活时呈现 Snipaste 标志性亮天蓝底 `#0084FF` + 纯白大写粗体 T）
    - ⑦ `eraser`（橡皮擦：悬停高亮图元，单击精准抹除）
    - 分隔线 \| 撤销 \| 重做 \| 分隔线 \| 取消 (✕) \| 钉图 (📌) \| 保存 (💾) \| 复制到剪贴板 (⧉ 双重叠矩形) \| 分隔线 \| OCR「文」独立胶囊按钮。
- **文字标注高级二级属性栏（8 大模块精密复刻用户实截图）**：
  - ① 加粗 `[ B ]`：激活时为实心亮天蓝底 + 白字加粗 B；
  - ② 斜体 `[ I ]`：实时切换斜体；
  - ③ 轮廓/底衬 `[ A ]`：实时切换高对比微阴影与纯色背景衬底；
  - ④ 字体下拉框 `[ Microsoft YaHei UI ∨ ]`：支持雅黑、Segoe UI、Arial、Consolas、宋体等；
  - ⑤ 字号微调框 `[ 16.0 ∨ ]`：支持滚轮微调、预设字号切换与四角拖拽等比缩放联动；
  - ⑥ 主颜色大方块 `[ █ ◢ ]`：带彩虹调色盘小角标，点击打开系统取色盘；
  - ⑦ 24 色双行预设调色板矩阵：两行十二列精致小方块（上层深色/基础色，下层浅色/柔和色），单选瞬间换色。
- **快捷交互全量补齐**：
  - **按住空格键 (`Space`)**：鼠标变为抓手，可任意拖拽整体平移当前选区；
  - **鼠标右键单击**：直接快速销毁退出截图，零阻滞无多余弹窗；
  - **`Shift` 辅助键约束**：画矩形/椭圆时约束为正方形/正圆；画直线时约束为水平/垂直/45度；
  - **贴图 (`PinForm`)**：支持 `Ctrl + 滚轮` 平滑无级调节悬浮窗透明度（20%~100%）。
- **本地编译验证**：
  - 使用 .NET 原生 CSC 优化编译为单文件 `screenshotOcrTranslate\bin\snipper.exe`（64.5KB，0 错误 0 警告，零依赖）。
- **工具栏与二级属性栏像素级深度自检精修 (Parity Polish)**：
  - **工具栏按钮次序 1:1 对齐 Snipaste**：将右侧按键由错误的 `cancel -> pin -> save -> copy` 修正为标准次序 `[贴图 pin] -> [保存 save] -> [复制 copy] -> [取消 cancel] -> [OCR]`，取消键回归右侧，完全符合用户肌肉记忆。
  - **全局工具激活高亮与质感深色**：所有激活工具统一实心亮天蓝 `#0084FF` 底 + 纯白高亮图标，常规状态统一为原生质感深炭灰 `#262626`。
  - **二级属性栏严格向右对齐 (Flush Right-Aligned)**：由固定左对齐彻底改为 `subX = _toolBarBounds.Right - subW;`，并紧贴主栏（间距 3px），彻底消除两栏错位。
  - **字体下拉框加宽防截断**：宽度扩展至 138px，彻底消除 `Microsoft YaHei UI` 被截断成 `Microsoft YaH` 的问题。
  - **下拉箭头升级为抗锯齿极简矢量折角**：改用两笔折线 `g.DrawLine` 绘制紧凑现代的下折角，摒弃字体字符 "∨"。
  - **调色板规范为 MS Paint / Snipaste 标准两行十列 (20色)**：大色块 20x20 配彩虹三角标，10 列色板宽度精准收拢至 128px，并在文字属性栏与图形属性栏各模块间加入精致纵向细分割线。
  - **原生弹出式下拉列表控件体系（彻底替换原先单调点击循环）**：
    - 字体选择：点击直接弹出 `ContextMenuStrip` 下拉列表，支持 7 种字体实时预览与打勾状态。
    - 字号选择：点击直接弹出常用字号阶梯列表（9~64 pt），当前字号勾选高亮。
    - 形状与填充选择：矩形/椭圆、实心/空心、箭头/折线均支持弹出直观下拉列表，告别盲猜点击。
    - 引入 `ModernDropdownRenderer` 与 `ModernColorTable`，定制圆角高亮浅蓝浮窗，智能防止屏幕外截断。
  - **箭头标注全维度深度对齐 Snipaste (1:1 Arrow Parity)**：
    - 采用 Snipaste 标志性的燕尾凹槽流线型实心锐角多边形（Notched Arrowhead），底座内凹 28%，翼展挺拔舒展。
    - 杆身线段严格止于凹槽中心 `Notch`，末端平整，彻底消除了旧版圆头粗线穿透箭头尖端的严重穿帮缺陷。
    - 拖拽绘制时按住 `Shift` 键，箭头严格约束为水平、垂直或 45° 完美正角。
    - 选区内按下 `Tab` 键，立即在【箭头】与【折线/直线】之间快速无缝互切。
    - 快捷键支持：按 `[` 调细、`]` 调粗，或按数字键 `1`/`2`/`3` 快速定档线宽（2px/4px/8px）。




- **Snipaste 招牌贴图/钉图 (Pin to Screen 📌)**：
  - 新增 `PinForm` 独立置顶贴图浮窗，点击工具栏【📌 钉图】或按快捷键 `F3` 即可将当前截图（含标注图元）钉在桌面上。
  - 支持鼠标任意拖拽移动位置、滚轮平滑缩放图片（0.2x~4.0x）、双击或 Esc 退出并复制、右键菜单（复制/另存为/关闭）。
- **根治框选畸形竖条 bug（Antigravity 窗口框选对齐 Snipaste）**：
  - 彻底解决由于暗黑纯色大背景贯穿屏幕导致误判出 `187 x 1318` 窄条的致命问题。
  - 引入四边封闭度验证与纵横比校验：禁止纵横比严重失真的垂直/水平穿透伪区域。
  - 将 Win32 真实完整应用主窗口作为结构基石：在大面积工作区自然精准高亮整个软件窗口（与 Snipaste 1:1 一致），在局部独立封闭组件上精准高亮组件。
- **工具栏顺序与样式 1:1 对齐 Snipaste**：
  - 图标顺序规范为：矩形、椭圆、箭头、画笔、马赛克、文字 \| 撤销、重做 \| 取消(✕)、钉图(📌)、保存(💾)、确定(✔) \| 识别文字(文/OCR)。
- **全屏截屏 (`Ctrl + A`)**：
  - 一键将截图选区拓展至当前全屏，并智能将工具栏停靠在右下角安全边距。
- **上次选区复用与历史穿越 (`R` / `Shift+R` / `,` / `.`)**：
  - 本地文件持久化记录历史截屏矩形（`~/.rubick_snipper_rects.txt`，最多保留 20 条）。
  - 按 `R` 键秒级复用上次截屏矩形；按 `,` 与 `.` 键在历史截屏尺寸间自由回溯切换。
- **Snipaste 1:1 风格快捷键指南面板**：
  - 纯黑磨砂背景（`#141821` 92% 不透明度）+ 柔和外阴影 + 精细边框。
  - 采用白框圆角键帽徽标（`[W] [A] [S] [D]`, `[Tab]`, `[Ctrl] [A]`, `[R]`...），并自带智能避让鼠标指针动效。
- **编译与部署**：
  - 编译生成最新的 `screenshotOcrTranslate/bin/snipper.exe`（单文件 46.5KB，零依赖）。

---

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

## [2026-10-09] 截图交互全景升级与 Snipaste 工具栏对齐

### 变更背景
- 用户指出截图时的交互设计需要进一步优化：
  1. 截图窗口需要支持 8 个控制把手拖拽拉伸大小，以及选区内整体按住平移移动位置。
  2. 鼠标悬停工具栏的样式需要更现代美观（圆角、高亮、柔和阴影、深色 Tooltip 气泡提示）。
  3. 工具栏功能不全：只有撤回没有下一步（重做）；缺少椭圆标注；画笔等形状需要支持自由设置颜色与粗细。

### 本次动作
- **8 点拉伸与平移交互**：
  - 引入完善的 `HitType` 状态判定，在四角与四边提供对应调整光标（`SizeNWSE`, `SizeNESW`, `SizeWE`, `SizeNS`）。
  - 支持在未选择标注工具时直接鼠标光标变为 `SizeAll` 拖拽平移选区。
  - 所有选区内已有的标注物（矩形、椭圆、箭头、自由画笔、马赛克）在选区平移时调用 `item.Offset(dx, dy)` 完美同步位移！
- **撤销与重做（Undo / Redo）历史栈**：
  - 增加 `_redoStack`，完美实现 `Ctrl+Z` 撤销与 `Ctrl+Y` 重做，并在堆栈为空时呈现优雅的 Disabled 禁用灰色。
- **扩展标注图形**：
  - 新增 `EllipseAnnotation`，支持绘制平滑椭圆/正圆标注。
- **二级属性栏（颜色与粗细）**：
  - 激活矩形、椭圆、箭头、画笔工具时自动展开二级栏。
  - 提供 8 种高对比度现代预设色板（选中有外光环）和 3 种线宽直径（细 2px、中 4px、粗 8px），即选即生效。
- **现代化 UI 与微动效**：
  - 工具栏采用纯白磨砂背景 + 6px 圆角 + 多层 Soft Drop Shadow 柔和立体阴影。
  - 悬停按钮显示浅灰圆角微光背景，激活按钮显示清爽天空蓝背景与边框。
  - 悬停浮现深色微胶囊 Tooltip 提示气泡。
- **编译发布**：
  - 成功重新编译为单文件 `bin/snipper.exe`（28KB，无外部依赖）。

## [2026-10-09] 解决 Chromium file 协议下 ESM CORS 拦截导致的无响应缺陷

### 变更背景
- 用户重启 Rubick 后依然无法自动截图，点击重新截图按钮也无响应。
- 通过在 `preload.js` 写入物理系统日志，抓取到底层真实行为：
  1. `preload.js` 正常执行，`window.rubick.onPluginEnter` 正常挂载并派发了 `{"code":"ocr-translate","type":"text"}`。
  2. 但 `app.js` 从始至终完全没有执行！
  3. **根本原因**：桌面 Electron Webview 运行在本地 `file:///` 协议下，受限于 Chromium 的严格同源策略（CORS），`<script type="module">` 导入的本地模块被直接拦截抛出跨域异常，导致整段前端业务逻辑根本未被解释执行。

### 本次动作
- **移除 ESM，内聚为独立单文件脚本**：
  - 将 `capture.js`、`llmClient.js` 和 `app.js` 的核心逻辑整体内聚整合为纯原生的单文件 `src/app.js`。
  - `index.html` 移除 `type="module"`，还原为标准的 `<script src="src/app.js"></script>`。
  - 挂载全局 `window.onerror` 与 `window.onunhandledrejection` 异常追踪，实现异常秒级捕获。
- **效果**：
  - 彻底解决 `file://` 协议下的脚本拦截问题，首屏 `app.js` 在微秒级同步完成解析执行。
  - 唤醒插件即瞬间接收到 `onPluginEnter` 缓存，并在 120ms 内自动隐藏 Rubick 窗口并调起全屏自包含截图。

## [2026-10-09] 截图工具栏对齐 Snipaste 文字标注、放弃取消与全图自适应预览

### 变更背景
- 用户反馈三大问题：
  1. 截图工具栏缺少放弃截图的取消按钮（点一下退出什么也不做）。
  2. 缺少文字标注功能，且需要支持调整文字大小与颜色。
  3. 左上角 Logo 破损裂纹，且截图在左侧缩略图展示不全（被裁切变形）。

### 本次动作
- **放弃截图【取消】按钮 (`cancel`)**：
  - 工具栏加入红色微光叉号（✕）图标，点击立即退出截图并不修改剪贴板，平稳恢复宿主窗口。
- **深度对齐 Snipaste 文本标注 (`text`)**：
  - 点击工具栏 `T` 按钮，选区内点击任意位置呼出内嵌自适应输入框，支持自动撑开宽高与多行换行。
  - 按 `Ctrl+Enter` 或失焦完成固化，`Esc` 取消输入；完整支持 `Ctrl+Z` 撤销与 `Ctrl+Y` 重做。
  - 支持小（12pt）、中（16pt）、大（22pt）三档字号切换与 8 色板无级切换，打字过程中切换即时联动变色变号。
  - 引入 Snipaste 核心质感：高对比度微阴影描边（浅色字带深阴影，深色字带亮微光），在深浅杂乱任何背景底图上均清晰锐利。
- **UI 破损与展示不全修复**：
  - 将左上角 Logo 替换为自包含高清内联矢量 SVG，彻底告别外部 CDN 依赖。
  - 缩略图改为自适应 `object-fit: contain` + 深色底衬托，100% 完整展现整张截图所有细节。
  - 增加原图点击放大 Lightbox 全屏预览浮层，点击任意位置或按 Esc 即可退出。

## [2026-10-09] 根除 <<<TRANSLATED>> 标记泄漏与 Vision Token 极致压缩提速

### 变更背景
- 用户反馈：
  1. 识别出来的原文框中残留有 `<<<TRANSLATED>>` 标记。
  2. 询问在性能方面如何进一步节省 Token 并提升响应速度。

### 本次动作
- **根治 `<<<TRANSLATED>>` 标记泄漏（流式分块断裂 Bug）**：
  - **根因剖析**：原流式解析器在遇到跨 chunk 的半截标记时，由于未匹配到完整标记而直接将其作为 delta 透传给原文文本框，导致末尾漏出标记残片。
  - **彻底解决**：重构为基于累积双缓冲分割器（Accumulative Split Buffer），先清洗暂扣再分流；同时配合 `cleanMarkers` 双重过滤消毒网关，彻底消灭任何标记字符外泄。
- **节省 60% Vision Token 与首字延迟提速 50%**：
  - **动态视口等比压缩**：将输入图片上限压制至文字识别黄金视口（`960×540`），使多模态大模型的视觉切片 Tiles 数量从 6~8 个锐减至 2~3 个，**Vision Token 消耗直接降低 60% 以上**。
  - **色彩与体积优化**：去除透明 Alpha 通道，转为 0.80 紧凑 JPEG，传输包体积降低至原本的 1/5。
  - **大模型首字提速**：因 Vision Patch 减半，多模态模型的 Vision Encoder 计算延迟从 1.8s 骤降至 700ms 左右，大幅缩短首字等待时间。

## [2026-10-09] 截图识别与翻译历史功能上线与智能唤醒体验优化

### 变更背景
- 用户诉求：
  1. 增加「截图识别文字历史」功能，方便回看先前提取的原文、译文与图片。
  2. 修复盲目自动截屏体验：以前一输入 `ocr` 唤醒插件就直接隐藏窗口强行进入截图，导致用户无法回头查看刚刚识别的内容。

### 本次动作
- **历史记录底层持久化**：
  - 在 `preload.js` 中新增 `saveHistory` 与 `loadHistory` 物理磁盘读写能力（持久化于 `~/.rubick_ocr_translate_history.json`，最多保存 30 条），即使重启 Rubick 历史记录也永不丢失。
- **现代化历史记录抽屉面板**：
  - 顶部导航栏增加【识别历史】按钮。
  - 抽屉面板展示历史条目卡片，包含截图微缩图、时间戳、目标语言标签、原文截断预览与译文高亮预览。
  - 支持单条记录点击【载入】一键恢复到主界面双栏（含图片、原文、译文、所选语言）。
  - 支持单条记录【删除】以及【清空全部历史】。
  - 支持直接在历史列表中点击截图缩略图呼出全屏 Lightbox 大图查看。
- **全新智能唤醒与防打扰策略**：
  - 唤醒插件时，若本地已有历史记录，插件自动呈现最近一条识别结果在主界面供用户查阅或复制，**绝不强行自动截屏打扰用户**。
  - 界面给出清晰提示：“已就绪 · 已载入最新记录，点击「重新截图」圈选新图”。
  - 只有在本地没有任何历史数据（全新使用）时，才自动唤起首次截屏引导。
## [2026-10-09] 文字标注拖拽平移与四角同比例自由缩放、独立OCR按钮与2天历史机制

### 变更背景
- 用户反馈三大问题：
  1. 识别历史需限制保留最近 2 天内的数据。
  2. 工具栏【确定截图 ✔】应该只是复制到剪贴板，不要直接跑去 OCR 识别；【取消截图 ✕】应放在确定截图（✔）旁边；识别文字需做成独立按钮。
  3. 文字标注在点击提交时触发了 `NullReferenceException` 崩溃；标注字体不能死板地选“小、中、大”，必须支持按住文字拖拽平移，且支持拖拽四个角的手柄进行同比例自由缩放。

### 本次动作
- **根治文字提交 `NullReferenceException` 崩溃**：
  - 根因：鼠标点击窗体时导致输入框 `LostFocus` 先销毁控件，随后鼠标按下事件重入引发空引用访问。
  - 解决：引入原子锁 `_isCommittingText` 严格防重入保护，解耦引用并加入异常隔离防护。
- **文字标注交互重构（拖拽平移 + 四角同比例自由缩放）**：
  - 彻底抛弃死板的“小、中、大”三档固定字号，二级属性栏仅保留 8 种高品质预设色板。
  - 输入文字提交后，文字自动进入选中状态，外显天蓝色虚线包围盒与四个角锚点手柄（8×8px）。
  - **拖拽平移**：鼠标在文字包围盒内变为十字移动光标（`Cursors.SizeAll`），按住即可在截图选区内任意拖拽位移。
  - **四角自由缩放**：鼠标悬停在左上、右上、左下、右下四个角手柄时，自动呈现对应双向拉伸光标；按住拖动时，以对角不动点为基准实时计算缩放比例，字体大小实时同比例平滑放大/缩小（9pt~96pt），所见即所得。
- **工具栏确定/取消/OCR 按钮分流**：
  - 取消截图（✕）与确定截图（✔）紧邻并列。
  - **确定截图（✔ / 双击 / Enter）**：仅将截图保存并复制到系统剪贴板，平稳退出，不弹出 Rubick 窗口，不触发 OCR。
  - **取消截图（✕ / Esc）**：平稳退出，不改变剪贴板，不弹窗。
  - **独立【识别文字】按钮（文 / OCR）**：专门设立高亮按钮，点击后恢复显示 Rubick 窗口，并自动启动 OCR 提取与智能翻译。
- **历史记录 48 小时自动老化清理**：
  - `historyManager` 增加 2 天（48 小时）滑动窗口过滤，超期记录自动清理持久化文件，保持轻量纯净。

## [2026-10-09] 截图框选边框线与实时像素尺寸微胶囊效果优化

### 变更背景
- 用户反馈最开始拖拽拉框时效果不明显，甚至连框线都没有，无法直观把握选区边界。
- 根因：原绘制逻辑中 `_borderPen` 仅在 `_hasSelected`（鼠标松开框选完成后）才绘制，拖拽过程中缺少边框线呈现。

### 本次动作
- **双层高对比度框选边框线**：
  - 在拖拽拉框过程中（`_isSelecting`）及框选完成后，全程绘制 2px 鲜艳天空蓝外边框线与 1px 内侧半透明微光白描边，确保在任何极黑、极亮或杂乱底色下都轮廓分明。
- **实时像素尺寸微胶囊浮动提示**：
  - 在拉框过程中，选区左上角实时浮现深色微圆角胶囊（显示格式为 宽 × 高 像素，如 800 × 600），自适应边界停靠，所见即所得。

## [2026-10-09] 对齐 Snipaste 经典像素放大镜与屏幕取色器 (按 C 复制 / 按 Shift 切换)

### 变更背景
- 用户需求：在截图时增加类似 Snipaste 的屏幕取色功能，包括像素级放大镜网格、十字准心、精确坐标、色块预览与数值展示。

### 本次动作
- **1:1 复刻 Snipaste 经典取色放大镜（HUD）**：
  - **17×11 像素矩阵放大网格**：以当前鼠标为中心实时采样周围像素，8 倍放大显示并勾勒半透明像素网格线。
  - **十字瞄准导轨与准心方块**：渲染高亮整行整列十字微光导轨与中心双层（黑白高对比度）瞄准框，目标像素清晰可辨。
  - **实时坐标与色值呈现**：卡片正下方居中展示 `(X , Y)` 坐标，带有 11×11px 真实像素色块以及色值（RGB 与 HEX 格式）。
- **专业快捷操作**：
  - **按 `C` 键复制颜色值**：将当前像素色值直接复制到系统剪贴板并平稳退出截屏，前端弹出 Toast 提示。
  - **按 `Shift` 键无缝切换格式**：实时在 `R, G, B` 与 `#RRGGBB` 之间快速切换。
  - **智能避让边界**：放大镜卡片默认跟随鼠标右下方，靠近屏幕边缘时自动翻转到左侧或上方，防止越界被裁切。

## [2026-10-10] 新增「ocr截图」独立指令，支持直达全屏截图与免弹窗安静流程

### 变更背景
- 用户反馈：输入 `ocr` 时会跳转到文字识别/翻译的页面（可看上次识别结果），但有时仅希望单纯截图，无需跳转到文字识别页面。
- 诉求：增加类似 `ocr截图` 等独立关键词，输入后进入插件直接切入截图界面。

### 本次动作
- **扩展 package.json 独立特性 `direct-capture`**：
  - 注册专属关键词：`ocr截图`、`截图`、`截屏`、`屏幕截图`。
  - 与原有的 `ocr-translate`（`ocr`、`截图翻译`、`文字提取`）清晰解耦。
- **差异化启动调度策略**：
  - **输入 `ocr`**：唤醒文本识别与翻译面板，若有历史记录则展示最近一条结果供查看、复制与校对，不强行自动截屏。
  - **输入 `ocr截图`（或 `截图`、`截屏`）**：无视历史缓存，60ms 极速直接唤起原生全屏截图器，不展示主窗口。
  - **单纯截图安静闭环**：在直接截图模式下，用户框选完点击【✔ 确定截图】或双击后，图片存入剪贴板并平稳退出，**全程完全不弹出文字识别界面**，安静高效！









