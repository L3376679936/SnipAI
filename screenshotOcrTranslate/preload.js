const screenshot = require('screenshot-desktop');
const { clipboard, nativeImage } = require('electron');
const { exec } = require('child_process');

/**
 * 宿主与底层能力桥接器
 */
window.nativeBridge = {
  /**
   * 优先调用 Rubick 原生 screenCapture，若不支持则调用 Windows 原生选区截图
   */
  captureScreen: () => {
    return new Promise((resolve, reject) => {
      // 1. 尝试 Rubick / uTools 原生截屏 API
      const hostScreenCapture =
        (window.rubick && typeof window.rubick.screenCapture === 'function' && window.rubick.screenCapture) ||
        (window.utools && typeof window.utools.screenCapture === 'function' && window.utools.screenCapture);

      if (hostScreenCapture) {
        try {
          hostScreenCapture((base64) => {
            if (base64) {
              resolve(base64);
            } else {
              reject(new Error('用户取消了截图'));
            }
          });
          return;
        } catch (e) {
          console.warn('调用宿主 screenCapture 失败，转为系统截图:', e);
        }
      }

      // 2. Windows 系统原生 Snipping Tool 区域截图（Win+Shift+S 底层协议）
      if (process.platform === 'win32') {
        window.nativeBridge.hideMainWindow();

        // 记录截图前的剪贴板状态
        const prevImg = clipboard.readImage();
        const prevDataUrl = prevImg && !prevImg.isEmpty() ? prevImg.toDataURL() : '';

        // 呼起 Windows 原生划选截屏
        exec('explorer.exe ms-screenclip:');

        const startTime = Date.now();
        const timer = setInterval(() => {
          try {
            const currentImg = clipboard.readImage();
            if (currentImg && !currentImg.isEmpty()) {
              const currentDataUrl = currentImg.toDataURL();
              // 检测到剪贴板出现了新截取的图片
              if (currentDataUrl && currentDataUrl !== prevDataUrl) {
                clearInterval(timer);
                window.nativeBridge.showMainWindow();
                resolve(currentDataUrl);
                return;
              }
            }
          } catch (err) {}

          // 30 秒超时
          if (Date.now() - startTime > 30000) {
            clearInterval(timer);
            window.nativeBridge.showMainWindow();
            reject(new Error('截图超时或已取消'));
          }
        }, 350);
        return;
      }

      // 3. 兜底方案：全屏抓取底图
      screenshot({ format: 'png' })
        .then((buf) => {
          resolve(`data:image/png;base64,${buf.toString('base64')}`);
        })
        .catch((err) => {
          reject(err);
        });
    });
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
   */
  onPluginEnter: (callback) => {
    const handler = (action) => {
      callback && callback(action);
    };

    if (window.rubick && typeof window.rubick.onPluginEnter === 'function') {
      window.rubick.onPluginEnter(handler);
    } else if (window.utools && typeof window.utools.onPluginEnter === 'function') {
      window.utools.onPluginEnter(handler);
    }
  }
};
