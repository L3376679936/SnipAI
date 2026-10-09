const screenshot = require('screenshot-desktop');
const { clipboard } = require('electron');
const { exec } = require('child_process');

/**
 * 宿主与底层能力桥接器
 */
window.nativeBridge = {
  /**
   * 唤起系统级划选截屏并自动感知剪贴板载入
   */
  captureScreen: () => {
    return new Promise((resolve, reject) => {
      // 1. 隐藏 Rubick 窗口，给屏幕让出视野
      window.nativeBridge.hideMainWindow();

      // 2. 记录截图前剪贴板中的图片
      let prevDataUrl = '';
      try {
        const prevImg = clipboard.readImage();
        if (prevImg && !prevImg.isEmpty()) {
          prevDataUrl = prevImg.toDataURL();
        }
      } catch (e) {
        console.warn('获取前置剪贴板失败:', e);
      }

      // 3. 唤起 Windows 原生划选截屏（十字光标区域选择）
      if (process.platform === 'win32') {
        exec('start ms-screenclip:', (err) => {
          if (err) {
            console.warn('通过 start 唤起失败，尝试 explorer:', err);
            exec('explorer.exe ms-screenclip:');
          }
        });

        const startTime = Date.now();
        const checkTimer = setInterval(() => {
          try {
            const currentImg = clipboard.readImage();
            if (currentImg && !currentImg.isEmpty()) {
              const currentDataUrl = currentImg.toDataURL();
              // 检测到剪贴板出现了新的截图图片
              if (currentDataUrl && currentDataUrl !== prevDataUrl) {
                clearInterval(checkTimer);
                window.nativeBridge.showMainWindow();
                resolve(currentDataUrl);
                return;
              }
            }
          } catch (err) {}

          // 40秒超时保护
          if (Date.now() - startTime > 40000) {
            clearInterval(checkTimer);
            window.nativeBridge.showMainWindow();
            reject(new Error('截图未完成或已取消'));
          }
        }, 300);

        return;
      }

      // 非 Windows 平台回退方案
      screenshot({ format: 'png' })
        .then((buf) => {
          window.nativeBridge.showMainWindow();
          resolve(`data:image/png;base64,${buf.toString('base64')}`);
        })
        .catch((err) => {
          window.nativeBridge.showMainWindow();
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
