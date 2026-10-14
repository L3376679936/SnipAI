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
   * 唤起 uTools 官方原生全屏选区截屏 (纯 JS API 实现，零外部可执行二进制，100% 符合市场审核规范)
   */
  captureScreen: () => {
    return new Promise((resolve, reject) => {
      logDebug('captureScreen 被调用 (uTools 官方原生纯 JS 截屏通道)');

      // 1. 优先调用 uTools 原生截屏 API
      if (window.utools && typeof window.utools.screenCapture === 'function') {
        window.nativeBridge.hideMainWindow();
        window.utools.screenCapture((base64Str) => {
          window.nativeBridge.showMainWindow();
          window.nativeBridge.setHeight(600);

          if (!base64Str) {
            logDebug('用户取消了 uTools 截屏');
            reject(new Error('截图已取消'));
            return;
          }

          const dataUrl = base64Str.startsWith('data:') ? base64Str : `data:image/png;base64,${base64Str}`;
          logDebug(`uTools 原生截屏成功，生成图片 DataURL (字符数: ${dataUrl.length})`);

          // 自动复制到剪贴板，方便用户在其他地方粘贴
          try {
            if (window.utools.copyImage) {
              window.utools.copyImage(dataUrl);
            }
          } catch (e) {}

          const bundle = {
            cleanDataUrl: dataUrl,
            annotatedDataUrl: dataUrl,
            maskDataUrl: null,
            hasMaskRegion: false,
            textNotes: [],
            boxes: [],
            timestamp: Date.now()
          };

          resolve({
            action: 'ready',
            dataUrl: dataUrl,
            bundle: bundle
          });
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


