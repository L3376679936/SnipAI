const screenshot = require('screenshot-desktop');
const { clipboard } = require('electron');

/**
 * 宿主与底层能力桥接器
 */
window.nativeBridge = {
  /**
   * 捕获屏幕全屏底图并转为 Base64
   */
  captureFullScreen: async () => {
    try {
      const imgBuffer = await screenshot({ format: 'png' });
      return `data:image/png;base64,${imgBuffer.toString('base64')}`;
    } catch (err) {
      console.error('屏幕捕获异常:', err);
      throw err;
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
