const path = require('path');
const fs = require('fs');
const os = require('os');
const { clipboard, nativeImage } = require('electron');

// 统一保存在用户主目录下的持久化配置文件，跨会话/跨重启永不丢失
const CONFIG_FILE_PATH = path.join(os.homedir(), '.rubick_ocr_translate_config.json');
const HISTORY_FILE_PATH = path.join(os.homedir(), '.rubick_ocr_translate_history.json');

const LOG_FILE = path.join(os.homedir(), '.rubick_snipper_debug.log');
function logDebug(msg) {
  try {
    fs.appendFileSync(LOG_FILE, `[${new Date().toLocaleTimeString()}] ${msg}\n`);
  } catch (e) {}
}

// 历史记录的异步落盘状态（防抖窗口内只保留最后一份待写数据）
let pendingHistory = null;
let historySaveTimer = null;
function flushHistoryToDisk() {
  if (historySaveTimer) {
    clearTimeout(historySaveTimer);
    historySaveTimer = null;
  }
  if (pendingHistory === null) return;
  const snapshot = pendingHistory;
  pendingHistory = null;
  try {
    fs.writeFile(HISTORY_FILE_PATH, JSON.stringify(snapshot, null, 2), 'utf-8', (err) => {
      if (err) logDebug(`写入历史记录失败: ${err.message}`);
    });
  } catch (err) {
    logDebug(`写入历史记录失败: ${err.message}`);
  }
}
// 渲染进程退出前同步刷盘：防抖窗口内若正好退出，异步写来不及完成会丢数据
process.on('exit', () => {
  if (historySaveTimer && pendingHistory !== null) {
    try {
      fs.writeFileSync(HISTORY_FILE_PATH, JSON.stringify(pendingHistory, null, 2), 'utf-8');
    } catch (e) {}
  }
});

logDebug(`Preload 初始化: __dirname=${__dirname}`);
try {
  const electron = require('electron');
  const modKeys = Object.keys(electron);
  logDebug(`Electron 模块清单: ${modKeys.join(', ')}`);
  if (electron.desktopCapturer) {
    logDebug(`desktopCapturer 存在: ${typeof electron.desktopCapturer.getSources}`);
  } else {
    logDebug('desktopCapturer 不在渲染进程');
  }
} catch (e) {
  logDebug(`探测 Electron 失败: ${e.message}`);
}

/**
 * 宿主与底层能力桥接器 (纯内置自包含实现，无任何外部软件依赖)
 */
window.nativeBridge = {
  log: logDebug,

  /**
   * 持久化保存大模型配置到本地磁盘文件
   */
  saveConfig: (cfg) => {
    try {
      fs.writeFileSync(CONFIG_FILE_PATH, JSON.stringify(cfg, null, 2), 'utf-8');
      return true;
    } catch (err) {
      logDebug(`写入配置失败: ${err.message}`);
      return false;
    }
  },

  /**
   * 从本地磁盘文件读取大模型配置
   */
  loadConfig: () => {
    try {
      if (fs.existsSync(CONFIG_FILE_PATH)) {
        const text = fs.readFileSync(CONFIG_FILE_PATH, 'utf-8');
        return JSON.parse(text);
      }
    } catch (err) {
      logDebug(`读取磁盘配置失败: ${err.message}`);
    }
    return null;
  },

  /**
   * 持久化保存历史识别记录到本地磁盘 (最多保存 30 条)
   *
   * 历史里带 base64 截图，JSON 体积可达数 MB：原先的 writeFileSync 会在渲染进程
   * 主线程上同步落盘，每次删除/新增都卡一次 UI。这里改为异步写 + 200ms 防抖，
   * 连续操作只落一次盘；渲染进程退出时再同步兜底刷一次，避免丢最后一条。
   */
  saveHistory: (list) => {
    try {
      pendingHistory = list || [];
      if (historySaveTimer) clearTimeout(historySaveTimer);
      historySaveTimer = setTimeout(flushHistoryToDisk, 200);
      return true;
    } catch (err) {
      logDebug(`写入历史记录失败: ${err.message}`);
      return false;
    }
  },

  /**
   * 从本地磁盘读取历史识别记录
   */
  loadHistory: () => {
    try {
      if (fs.existsSync(HISTORY_FILE_PATH)) {
        const text = fs.readFileSync(HISTORY_FILE_PATH, 'utf-8');
        return JSON.parse(text);
      }
    } catch (err) {
      logDebug(`读取历史记录失败: ${err.message}`);
    }
    return [];
  },

  /**
   * 唤起纯 JS 全屏交互选区截屏与悬浮工具栏 (100% 纯 Web/Canvas 实现，零 EXE 外部依赖，完全符合市场审核)
   */
  captureScreen: (options = {}) => {
    return new Promise((resolve, reject) => {
      logDebug('captureScreen 被调用 (纯 JS 悬浮工具栏交互截屏通道)');

      if (window.utools && typeof window.utools.screenCapture === 'function') {
        window.nativeBridge.hideMainWindow();

        // 1. 采集屏幕选区底图
        window.utools.screenCapture((base64Str) => {
          if (!base64Str) {
            logDebug('用户取消了原生截屏');
            window.nativeBridge.showMainWindow();
            reject(new Error('截图已取消'));
            return;
          }

          const dataUrl = base64Str.startsWith('data:') ? base64Str : `data:image/png;base64,${base64Str}`;
          logDebug(`屏幕采样成功 (数据大小: ${dataUrl.length})，立即唤起悬浮工具栏交互层`);

          // 2. 清理临时 IPC 与初始化文件
          const CAPTURE_IPC_FILE = path.join(os.tmpdir(), 'rubick_capture_ipc_result.json');
          const CAPTURE_INIT_FILE = path.join(os.tmpdir(), 'rubick_capture_init.json');
          try { if (fs.existsSync(CAPTURE_IPC_FILE)) fs.unlinkSync(CAPTURE_IPC_FILE); } catch (e) {}
          try { if (fs.existsSync(CAPTURE_INIT_FILE)) fs.unlinkSync(CAPTURE_INIT_FILE); } catch (e) {}

          // 3. 计算用户截图所在的屏幕位置与 DPI 缩放比，精准换算逻辑尺寸
          const point = (window.utools.getCursorScreenPoint && window.utools.getCursorScreenPoint()) || { x: 0, y: 0 };
          const display = (window.utools.getDisplayNearestPoint && window.utools.getDisplayNearestPoint(point)) || {
            bounds: { x: 0, y: 0, width: 1920, height: 1080 },
            scaleFactor: 1
          };
          const scale = display.scaleFactor || 1;

          // 解析截图物理像素尺寸
          let physW = 800;
          let physH = 600;
          try {
            const { nativeImage } = require('electron');
            const nImg = nativeImage.createFromDataURL(dataUrl);
            const sz = nImg.getSize();
            if (sz && sz.width > 0 && sz.height > 0) {
              physW = sz.width;
              physH = sz.height;
            }
          } catch (e) {
            logDebug(`nativeImage 提取尺寸异常: ${e.message}`);
          }

          // 核心修复：必须除以 scaleFactor 换算为屏幕 DIP (逻辑像素)！彻底杜绝图片自动放大全屏与工具栏掉出屏幕！
          const logicalW = Math.max(10, Math.round(physW / scale));
          const logicalH = Math.max(10, Math.round(physH / scale));

          // 工具栏预留宽度与高度
          const TOOLBAR_WIDTH = 530;
          const TOOLBAR_HEIGHT = 86;
          const winWidth = Math.max(logicalW, TOOLBAR_WIDTH);
          const winHeight = logicalH + TOOLBAR_HEIGHT + 14;

          // 按照用户截图终点（光标点）反推窗口左上角
          let winX = point.x - logicalW;
          let winY = point.y - logicalH;

          // 限制在当前显示器边界内，绝不跨屏跳跃到主屏
          const db = display.bounds || { x: 0, y: 0, width: 1920, height: 1080 };
          if (winX < db.x) winX = db.x;
          if (winX + winWidth > db.x + db.width) winX = Math.max(db.x, db.x + db.width - winWidth);
          if (winY < db.y) winY = db.y;
          if (winY + winHeight > db.y + db.height) winY = Math.max(db.y, db.y + db.height - winHeight);

          logDebug(`悬浮工具栏精准定位: [${winX}, ${winY}], 尺寸: ${winWidth}x${winHeight}, scale=${scale}, 屏幕: ${JSON.stringify(db)}`);

          // 4. 写入本地初始化文件，实现 100% 同步且防竞争的数据注入
          const initPayload = {
            dataUrl: dataUrl,
            physW: physW,
            physH: physH,
            logicalW: logicalW,
            logicalH: logicalH,
            scale: scale
          };
          try {
            fs.writeFileSync(CAPTURE_INIT_FILE, JSON.stringify(initPayload), 'utf-8');
          } catch (initErr) {
            logDebug(`写入 CAPTURE_INIT_FILE 异常: ${initErr.message}`);
          }

          // 5. 弹出置顶透明截图标注窗口 capture.html (指定当前屏幕坐标 + 100% 透明背景色)
          let captureWin = null;
          try {
            captureWin = window.utools.createBrowserWindow('capture.html', {
              title: '截图标注与悬浮工具栏',
              x: Math.round(winX),
              y: Math.round(winY),
              width: Math.round(winWidth),
              height: Math.round(winHeight),
              frame: false,
              alwaysOnTop: true,
              transparent: true,
              backgroundColor: '#00000000', // 100% 纯透明底色，彻底消除白屏与白蒙版！
              skipTaskbar: true,
              resizable: false,
              minimizable: false,
              useContentSize: true
            }, () => {
              if (captureWin && captureWin.webContents) {
                captureWin.webContents.executeJavaScript(`
                  if (window.loadFromInitData) {
                    window.loadFromInitData(${JSON.stringify(initPayload)});
                  }
                `);
              }
            });
          } catch (winErr) {
            logDebug(`创建 capture.html 失败: ${winErr.message}，降级回退`);
            window.nativeBridge.showMainWindow();
            resolve({
              action: 'ready',
              dataUrl: dataUrl,
              bundle: {
                cleanDataUrl: dataUrl,
                annotatedDataUrl: dataUrl,
                boxes: [],
                hasMaskRegion: false,
                timestamp: Date.now()
              }
            });
            return;
          }

          // 4. 处理截屏层返回的结果
          let isHandled = false;
          const onFinish = () => {
            if (isHandled) return;
            isHandled = true;

            // 读取 IPC 结果
            let result = null;
            try {
              if (fs.existsSync(CAPTURE_IPC_FILE)) {
                const text = fs.readFileSync(CAPTURE_IPC_FILE, 'utf-8');
                result = JSON.parse(text);
              }
            } catch (e) {}

            window.nativeBridge.showMainWindow();
            window.nativeBridge.setHeight(620);

            if (result && result.action && result.action !== 'cancel') {
              logDebug(`悬浮工具栏成功触发动作: ${result.action}`);
              const exportData = result.data || {};
              resolve({
                action: result.action,
                dataUrl: exportData.cleanDataUrl || dataUrl,
                bundle: exportData.bundle || {
                  cleanDataUrl: exportData.cleanDataUrl || dataUrl,
                  annotatedDataUrl: exportData.annotatedDataUrl || dataUrl,
                  boxes: exportData.bundle?.boxes || [],
                  hasMaskRegion: (exportData.bundle?.boxes?.length || 0) > 0,
                  timestamp: Date.now()
                }
              });
            } else if (result && result.action === 'cancel') {
              logDebug('用户在悬浮工具栏取消了截图');
              reject(new Error('截图已取消'));
            } else {
              // 兜底直接返回截取的数据
              resolve({
                action: 'ready',
                dataUrl: dataUrl,
                bundle: {
                  cleanDataUrl: dataUrl,
                  annotatedDataUrl: dataUrl,
                  boxes: [],
                  hasMaskRegion: false,
                  timestamp: Date.now()
                }
              });
            }
          };

          if (captureWin && typeof captureWin.on === 'function') {
            captureWin.on('closed', onFinish);
          } else {
            const checkTimer = setInterval(() => {
              if (fs.existsSync(CAPTURE_IPC_FILE)) {
                clearInterval(checkTimer);
                try { if (captureWin && captureWin.close) captureWin.close(); } catch (e) {}
                onFinish();
              }
            }, 80);
          }
        });
        return;
      }

      // 2. 兜底尝试读取系统剪贴板图片
      try {
        const img = clipboard.readImage();
        if (img && !img.isEmpty()) {
          window.nativeBridge.showMainWindow();
          const clipUrl = img.toDataURL();
          logDebug('通过剪贴板成功读取到截图');
          resolve({
            action: 'ocr',
            dataUrl: clipUrl,
            bundle: {
              cleanDataUrl: clipUrl,
              annotatedDataUrl: clipUrl,
              maskDataUrl: null,
              hasMaskRegion: false,
              textNotes: [],
              boxes: [],
              timestamp: Date.now()
            }
          });
          return;
        }
      } catch (clipErr) {
        logDebug(`剪贴板兜底异常: ${clipErr.message}`);
      }

      window.nativeBridge.showMainWindow();
      reject(new Error('未检测到 uTools 官方截屏环境，请在 uTools 客户端内运行'));
    });
  },

  /**
   * 获取最近一次 snipper.exe 生成的三图分离数据包（供「送去改图」或「剪贴板载入」自动恢复干净原图与红框坐标）
   */
  getLatestCaptureBundle: () => {
    try {
      const latestBundlePath = path.join(os.tmpdir(), 'rubick_latest_capture_bundle.json');
      if (fs.existsSync(latestBundlePath)) {
        const rawJson = fs.readFileSync(latestBundlePath, 'utf-8').replace(/^\uFEFF/, '').trim();
        const parsed = JSON.parse(rawJson);
        // 仅在 15 分钟内有效
        if (parsed && parsed.cleanDataUrl && (!parsed.timestamp || Date.now() - parsed.timestamp < 15 * 60 * 1000)) {
          return parsed;
        }
      }
    } catch (e) {
      logDebug(`getLatestCaptureBundle 失败: ${e.message}`);
    }
    return null;
  },

  /**
   * 直接从剪贴板读取最新图片 DataURL
   */
  readClipboardImage: () => {
    try {
      const img = clipboard.readImage();
      if (img && !img.isEmpty()) {
        return img.toDataURL();
      }
    } catch (e) {
      console.warn('剪贴板读取图片失败:', e);
    }
    return null;
  },

  /**
   * 将 DataURL 图片直接写入系统剪贴板
   */
  copyImageDataUrl: (dataUrl) => {
    try {
      if (window.utools && typeof window.utools.copyImage === 'function') {
        window.utools.copyImage(dataUrl);
        return true;
      }
      if (nativeImage && clipboard && typeof clipboard.writeImage === 'function') {
        const img = nativeImage.createFromDataURL(dataUrl);
        clipboard.writeImage(img);
        return true;
      }
    } catch (e) {
      logDebug(`写入图片到剪贴板失败: ${e.message}`);
    }
    return false;
  },

  /**
   * 将修改后的 DataURL 图片钉在桌面上变成置顶贴图 (纯 Web 实现，零 exe 依赖)
   */
  pinImageDataUrl: (dataUrl) => {
    try {
      if (!dataUrl) return false;
      if (window.utools && typeof window.utools.createBrowserWindow === 'function') {
        const pinWin = window.utools.createBrowserWindow('pin.html', {
          title: '贴图',
          frame: false,
          alwaysOnTop: true,
          transparent: true,
          skipTaskbar: false,
          resizable: true,
          useContentSize: true
        }, () => {
          if (pinWin && pinWin.webContents) {
            pinWin.webContents.executeJavaScript(`
              if (window.loadPinImage) window.loadPinImage(${JSON.stringify(dataUrl)});
            `);
          }
        });
        return true;
      }
      return false;
    } catch (e) {
      logDebug(`钉图到桌面失败: ${e.message}`);
      return false;
    }
  },

  /**
   * 隐藏 Rubick 窗口
   */
  hideMainWindow: () => {
    if (window.rubick && typeof window.rubick.hideMainWindow === 'function') {
      window.rubick.hideMainWindow();
    } else if (window.utools && typeof window.utools.hideMainWindow === 'function') {
      window.utools.hideMainWindow();
    }
  },

  /**
   * 显示/恢复 Rubick 窗口
   */
  showMainWindow: () => {
    if (window.rubick && typeof window.rubick.showMainWindow === 'function') {
      window.rubick.showMainWindow();
    } else if (window.utools && typeof window.utools.showMainWindow === 'function') {
      window.utools.showMainWindow();
    }
  },

  /**
   * 调整插件窗口高度
   */
  setHeight: (height) => {
    if (window.rubick && typeof window.rubick.setExpendHeight === 'function') {
      window.rubick.setExpendHeight(height);
    } else if (window.utools && typeof window.utools.setExpendHeight === 'function') {
      window.utools.setExpendHeight(height);
    }
  },

  /**
   * 写入剪贴板文本
   */
  copyText: (text) => {
    if (clipboard && clipboard.writeText) {
      clipboard.writeText(text);
    } else {
      navigator.clipboard.writeText(text);
    }
  },

  /**
   * 监听插件激活（包括由剪贴板图片直接唤起）
   * 采用提前捕获 + 缓存重放机制，杜绝因 ES Module 异步加载慢而错过 Rubick 首屏 onPluginEnter 事件
   */
  onPluginEnter: (callback) => {
    logDebug('前端注册 onPluginEnter, 已有缓存=' + !!_cachedEnterAction);
    _activeEnterCallback = callback;
    if (_cachedEnterAction && callback) {
      try {
        logDebug('立即重放缓存的 action 给前端');
        callback(_cachedEnterAction);
      } catch (e) {
        logDebug('执行缓存的 onPluginEnter 失败: ' + e.message);
      }
    }
  }
};

// 在 preload 加载的第一时间就挂载宿主监听
let _cachedEnterAction = null;
let _activeEnterCallback = null;

const globalEnterDispatcher = (action) => {
  logDebug('宿主底层派发 onPluginEnter: ' + JSON.stringify(action));
  _cachedEnterAction = action;
  if (_activeEnterCallback) {
    _activeEnterCallback(action);
  }
};

if (window.rubick && typeof window.rubick.onPluginEnter === 'function') {
  logDebug('挂载 window.rubick.onPluginEnter 成功');
  window.rubick.onPluginEnter(globalEnterDispatcher);
} else if (window.utools && typeof window.utools.onPluginEnter === 'function') {
  logDebug('挂载 window.utools.onPluginEnter 成功');
  window.utools.onPluginEnter(globalEnterDispatcher);
} else {
  logDebug('警告: 未找到 window.rubick 或 window.utools 的 onPluginEnter');
}


