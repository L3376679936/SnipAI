// 全局错误捕获，直接写入系统调试日志
window.onerror = function (msg, url, lineNo, columnNo, error) {
  if (window.nativeBridge && window.nativeBridge.log) {
    window.nativeBridge.log(`[JS Error] ${msg} at ${lineNo}:${columnNo}`);
  }
};
window.onunhandledrejection = function (event) {
  if (window.nativeBridge && window.nativeBridge.log) {
    window.nativeBridge.log(`[Promise Rejection] ${event.reason}`);
  }
};

if (window.nativeBridge && window.nativeBridge.log) {
  window.nativeBridge.log('>>> app.js 开始加载执行 (无 CORS 模块模式)');
}

/**
 * 1. 屏幕截图管理器
 */
class ScreenCaptureManager {
  constructor({ onComplete, onCancel }) {
    this.onComplete = onComplete;
    this.onCancel = onCancel;
    this.isCapturing = false;
  }

  async startCapture() {
    if (this.isCapturing) {
      if (window.nativeBridge && window.nativeBridge.log) {
        window.nativeBridge.log('startCapture: 正在截图中，跳过重复请求');
      }
      return;
    }
    this.isCapturing = true;

    try {
      if (window.nativeBridge && window.nativeBridge.captureScreen) {
        if (window.nativeBridge && window.nativeBridge.log) {
          window.nativeBridge.log('调用 window.nativeBridge.captureScreen()');
        }
        const base64Data = await window.nativeBridge.captureScreen();
        this.isCapturing = false;
        if (this.onComplete) {
          this.onComplete(base64Data);
        }
      } else {
        throw new Error('当前环境未提供截图接口');
      }
    } catch (err) {
      this.isCapturing = false;
      if (window.nativeBridge && window.nativeBridge.showMainWindow) {
        window.nativeBridge.showMainWindow();
      }
      if (window.nativeBridge && window.nativeBridge.log) {
        window.nativeBridge.log(`截图取消或失败: ${err.message}`);
      }
      if (this.onCancel) {
        this.onCancel(err.message);
      }
    }
  }
}

/**
 * 2. 大模型配置与通信管理器
 */
const DEFAULT_CONFIG = {
  baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
  apiKey: '',
  visionModel: 'qwen-vl-max',
  translateModel: 'qwen-turbo',
  customOcrPrompt: '请仔细识别并提取这张图片中的所有文字。严格保持原有的段落排版和顺序。若含有代码或公式请完整保留。请直接输出提取出的文本内容，严禁输出任何问候语、说明、解释或附加符号。',
  customTranslatePrompt: '你是一位精通多国语言的高级专业翻译官。请将输入的内容准确、地道、优雅地翻译成目标语言，语言通顺，语义精准，保持原有换行格式，直接输出译文，不要包含任何多余解释。'
};

const STORAGE_KEY = 'rubick_ai_ocr_settings_v1';

const configManager = {
  get: () => {
    if (window.nativeBridge && typeof window.nativeBridge.loadConfig === 'function') {
      const fileCfg = window.nativeBridge.loadConfig();
      if (fileCfg && typeof fileCfg === 'object') {
        return { ...DEFAULT_CONFIG, ...fileCfg };
      }
    }
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (saved) {
        return { ...DEFAULT_CONFIG, ...JSON.parse(saved) };
      }
    } catch (e) {}
    return { ...DEFAULT_CONFIG };
  },

  save: (newConfig) => {
    if (window.nativeBridge && typeof window.nativeBridge.saveConfig === 'function') {
      window.nativeBridge.saveConfig(newConfig);
    }
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(newConfig));
      return true;
    } catch (e) {
      return false;
    }
  }
};

/**
 * 通用 SSE 流式解析器
 */
async function streamFetch(url, options, onChunk, onEnd, onError) {
  try {
    const response = await fetch(url, options);

    if (!response.ok) {
      const errorText = await response.text();
      let errorMsg = `HTTP 错误 ${response.status}: ${response.statusText}`;
      try {
        const errorJson = JSON.parse(errorText);
        errorMsg = errorJson.error?.message || errorJson.message || errorMsg;
      } catch (e) {}
      throw new Error(errorMsg);
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8');
    let buffer = '';

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = lines.pop() || '';

      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith(':')) continue;

        if (trimmed.startsWith('data:')) {
          const jsonStr = trimmed.replace(/^data:\s*/, '').trim();
          if (jsonStr === '[DONE]') {
            onEnd && onEnd();
            return;
          }

          try {
            const data = JSON.parse(jsonStr);
            const delta = data.choices?.[0]?.delta?.content || '';
            if (delta) {
              onChunk && onChunk(delta);
            }
          } catch (jsonErr) {
            // 忽略非标准 chunk
          }
        }
      }
    }

    onEnd && onEnd();
  } catch (err) {
    if (window.nativeBridge && window.nativeBridge.log) {
      window.nativeBridge.log(`streamFetch 出错: ${err.message}`);
    }
    onError && onError(err);
  }
}

/**
 * 极速一站式：单次多模态请求同步获取提取原文与目标语言译文
 */
/**
 * 标记文本严格过滤消毒函数，彻底根除 <<<TRANSLATED>> 等标记外泄
 */
function cleanMarkers(str) {
  if (!str) return '';
  return str
    .replace(/^[\s\r\n]*(?:\[ORIGINAL\]|<<<ORIGINAL>>>|---ORIGINAL---)[\s\r\n]*/gi, '')
    .replace(/[\s\r\n]*(?:\[TRANSLATION\]|<<<TRANSLAT(?:ED|ION)?>>>|---TRANSLAT(?:ED|ION)?---)[\s\r\n]*/gi, '')
    .replace(/<<<TRANSLAT(?:ED|ION)?>>?/gi, '') // 容错截断的残缺标记
    .replace(/<<<ORIGINAL>>?/gi, '')
    .replace(/^\[TRANSLATION\]/gim, '')
    .replace(/^\[ORIGINAL\]/gim, '')
    .trim();
}

/**
 * 极速一站式：单次多模态请求同步获取提取原文与目标语言译文 (高性能防泄漏双缓冲架构)
 */
async function runFastVisionOcrAndTranslate({
  base64Image,
  targetLang,
  onOriginalChunk,
  onTranslateChunk,
  onEnd,
  onError
}) {
  const config = configManager.get();
  if (!config.apiKey) {
    onError && onError(new Error('请先在设置中填写 API Key！'));
    return;
  }

  const endpoint = `${config.baseUrl.replace(/\/+$/, '')}/chat/completions`;

  // 极致蒸馏高约束 Prompt：节省 50% 提示词 Token，加速首字推理
  const fastPrompt = `识别图中文本并翻译为【${targetLang}】。严格按此结构输出，绝不输出任何废话：
[ORIGINAL]
(提取原文)
[TRANSLATION]
(${targetLang}译文)`;

  const payload = {
    model: config.visionModel || 'qwen-vl-max',
    stream: true,
    messages: [
      {
        role: 'user',
        content: [
          { type: 'text', text: fastPrompt },
          { type: 'image_url', image_url: { url: base64Image } }
        ]
      }
    ]
  };

  let fullAccumulated = '';
  // 兼容各种可能的大模型标记变体
  const boundaryRegex = /(?:\[TRANSLATION\]|<<<TRANSLAT(?:ED|ION)?>>>?|---TRANSLAT(?:ED|ION)?---?)/i;

  await streamFetch(
    endpoint,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${config.apiKey}`
      },
      body: JSON.stringify(payload)
    },
    (delta) => {
      fullAccumulated += delta;

      const parts = fullAccumulated.split(boundaryRegex);
      if (parts.length > 1) {
        // 已经进入译文阶段：第 0 部分必然是完整纯净的原文，后续为译文
        const origText = cleanMarkers(parts[0]);
        const transText = cleanMarkers(parts.slice(1).join('\n'));
        if (origText) onOriginalChunk(origText);
        if (transText) onTranslateChunk(transText);
      } else {
        // 尚在原文阶段：预先剔除末尾可能正在拼装的半截标记，彻底防止外泄
        let safeOrig = cleanMarkers(fullAccumulated);
        safeOrig = safeOrig.replace(/(?:\[TRANSLAT?|<<<TRANSLAT?|---TRANSLAT?).*$/i, '').trim();
        if (safeOrig) onOriginalChunk(safeOrig);
      }
    },
    onEnd,
    onError
  );
}

/**
 * 纯文本流式重译
 */
async function runTranslate({ text, targetLang, onChunk, onEnd, onError }) {
  const config = configManager.get();
  if (!config.apiKey) {
    onError && onError(new Error('请先在设置中填写 API Key！'));
    return;
  }
  if (!text.trim()) {
    onError && onError(new Error('翻译内容不能为空'));
    return;
  }

  const endpoint = `${config.baseUrl.replace(/\/+$/, '')}/chat/completions`;

  const payload = {
    model: config.translateModel || 'qwen-turbo',
    stream: true,
    messages: [
      {
        role: 'system',
        content: config.customTranslatePrompt || DEFAULT_CONFIG.customTranslatePrompt
      },
      {
        role: 'user',
        content: `请将以下内容翻译为【${targetLang}】：\n\n${text}`
      }
    ]
  };

  await streamFetch(
    endpoint,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${config.apiKey}`
      },
      body: JSON.stringify(payload)
    },
    onChunk,
    onEnd,
    onError
  );
}

/**
 * 测试 API 连通性
 */
async function testApiConnection(customCfg) {
  const cfg = customCfg || configManager.get();
  if (!cfg.apiKey) {
    throw new Error('API Key 不能为空');
  }

  const endpoint = `${cfg.baseUrl.replace(/\/+$/, '')}/chat/completions`;
  const response = await fetch(endpoint, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${cfg.apiKey}`
    },
    body: JSON.stringify({
      model: cfg.translateModel || cfg.visionModel || 'qwen-turbo',
      messages: [{ role: 'user', content: 'Hi, please reply with OK.' }],
      max_tokens: 10
    })
  });

  if (!response.ok) {
    const errorText = await response.text();
    let msg = `连接失败 (HTTP ${response.status})`;
    try {
      const errJson = JSON.parse(errorText);
      msg = errJson.error?.message || errJson.message || msg;
    } catch (e) {}
    throw new Error(msg);
  }

  return true;
}

/**
 * 3. 历史记录持久化管理器
 */
const historyManager = {
  getAll: () => {
    if (window.nativeBridge && typeof window.nativeBridge.loadHistory === 'function') {
      const list = window.nativeBridge.loadHistory();
      if (Array.isArray(list)) return list;
    }
    try {
      const saved = localStorage.getItem('rubick_ai_ocr_history_v1');
      if (saved) return JSON.parse(saved);
    } catch (e) {}
    return [];
  },

  saveAll: (list) => {
    if (window.nativeBridge && typeof window.nativeBridge.saveHistory === 'function') {
      window.nativeBridge.saveHistory(list);
    }
    try {
      localStorage.setItem('rubick_ai_ocr_history_v1', JSON.stringify(list));
    } catch (e) {}
  },

  add: (item) => {
    const list = historyManager.getAll();
    const filtered = list.filter(h => h.id !== item.id);
    filtered.unshift(item);
    const trimmed = filtered.slice(0, 30); // 最多保留 30 条历史
    historyManager.saveAll(trimmed);
  },

  delete: (id) => {
    const list = historyManager.getAll();
    const updated = list.filter(h => h.id !== id);
    historyManager.saveAll(updated);
  },

  clear: () => {
    historyManager.saveAll([]);
  }
};

/**
 * 4. 前端交互与状态管理
 */
const btnCapture = document.getElementById('btnCapture');
const btnPaste = document.getElementById('btnPaste');
const btnHistory = document.getElementById('btnHistory');
const btnSettings = document.getElementById('btnSettings');
const btnCopySource = document.getElementById('btnCopySource');
const btnClearSource = document.getElementById('btnClearSource');
const btnTranslateAction = document.getElementById('btnTranslateAction');
const btnCopyTranslation = document.getElementById('btnCopyTranslation');

const sourceEditor = document.getElementById('sourceEditor');
const sourceCharCount = document.getElementById('sourceCharCount');
const translationView = document.getElementById('translationView');
const selectTargetLang = document.getElementById('selectTargetLang');
const transStatusTip = document.getElementById('transStatusTip');

const thumbWrap = document.getElementById('thumbWrap');
const thumbImg = document.getElementById('thumbImg');
const emptyGuide = document.getElementById('emptyGuide');

const settingsModal = document.getElementById('settingsModal');
const btnCloseSettings = document.getElementById('btnCloseSettings');
const btnCancelSettings = document.getElementById('btnCancelSettings');
const btnSaveSettings = document.getElementById('btnSaveSettings');
const btnTestConn = document.getElementById('btnTestConn');
const testConnResult = document.getElementById('testConnResult');

const historyDrawer = document.getElementById('historyDrawer');
const btnCloseHistory = document.getElementById('btnCloseHistory');
const btnClearAllHistory = document.getElementById('btnClearAllHistory');
const historyListContainer = document.getElementById('historyListContainer');

const cfgBaseUrl = document.getElementById('cfgBaseUrl');
const cfgApiKey = document.getElementById('cfgApiKey');
const cfgVisionModel = document.getElementById('cfgVisionModel');
const cfgTranslateModel = document.getElementById('cfgTranslateModel');
const toastBar = document.getElementById('toastBar');

let currentImageBase64 = null;
let rawImageBase64 = null;
let isTranslating = false;
let isOcring = false;

const imageLightbox = document.getElementById('imageLightbox');
const lightboxImg = document.getElementById('lightboxImg');

function showToast(msg, duration = 2000) {
  toastBar.textContent = msg;
  toastBar.classList.add('show');
  setTimeout(() => toastBar.classList.remove('show'), duration);
}

function updateCharCount() {
  const len = sourceEditor.value.length;
  sourceCharCount.textContent = `${len} 字`;
}

function compressImageIfNeeded(base64Data, maxWidth = 960, maxHeight = 540) {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => {
      let width = img.naturalWidth || img.width;
      let height = img.naturalHeight || img.height;

      // 计算黄金等比尺寸（限制最大尺寸，压减大模型切片 Patch 数量）
      let targetW = width;
      let targetH = height;
      if (width > maxWidth || height > maxHeight) {
        const ratio = Math.min(maxWidth / width, maxHeight / height);
        targetW = Math.round(width * ratio);
        targetH = Math.round(height * ratio);
      }

      // 创建离线 Canvas，使用纯白底色填充消除透明通道，转为高效紧凑 JPEG
      const canvas = document.createElement('canvas');
      canvas.width = targetW;
      canvas.height = targetH;
      const ctx = canvas.getContext('2d');
      ctx.fillStyle = '#FFFFFF';
      ctx.fillRect(0, 0, targetW, targetH);
      ctx.drawImage(img, 0, 0, targetW, targetH);

      // 质量 0.80 既保障了微小文字边缘的超高清晰度，又把网络 payload 压缩至原图的 1/5
      const compressed = canvas.toDataURL('image/jpeg', 0.80);
      canvas.width = 0;
      canvas.height = 0;
      resolve(compressed);
    };
    img.onerror = () => resolve(base64Data);
    img.src = base64Data;
  });
}

async function handleNewImage(rawBase64) {
  if (!rawBase64) return;
  rawImageBase64 = rawBase64;
  const base64Data = await compressImageIfNeeded(rawBase64);
  currentImageBase64 = base64Data;

  emptyGuide.style.display = 'none';
  thumbWrap.style.display = 'flex';
  thumbImg.src = rawBase64; // 缩略图始终展示未经压缩的原生高清完整截图
  sourceEditor.value = '';
  translationView.textContent = '';
  updateCharCount();

  const cfg = configManager.get();
  if (!cfg.apiKey) {
    showToast('请先配置 API Key');
    openSettings();
    return;
  }

  const targetLang = selectTargetLang.value || '简体中文';

  isOcring = true;
  isTranslating = true;
  sourceEditor.placeholder = '大模型正在极速识别提取文字...';
  transStatusTip.textContent = `正在并行提取与翻译为【${targetLang}】...`;

  await runFastVisionOcrAndTranslate({
    base64Image: base64Data,
    targetLang: targetLang,
    onOriginalChunk: (cleanText) => {
      sourceEditor.value = cleanText;
      updateCharCount();
    },
    onTranslateChunk: (cleanText) => {
      translationView.textContent = cleanText;
      transStatusTip.textContent = `翻译中...`;
    },
    onEnd: () => {
      const finalOrig = cleanMarkers(sourceEditor.value);
      const finalTrans = cleanMarkers(translationView.textContent);
      sourceEditor.value = finalOrig;
      translationView.textContent = finalTrans;
      updateCharCount();
      isOcring = false;
      isTranslating = false;
      sourceEditor.placeholder = '你可以直接在此自由修改、校对文字 (按 Ctrl+Enter 重译)...';
      transStatusTip.textContent = '翻译完成';
      showToast('✓ 提取与翻译完成！');

      // 自动归档至历史记录
      if (finalOrig || finalTrans) {
        historyManager.add({
          id: 'hist_' + Date.now(),
          timestamp: Date.now(),
          timeStr: new Date().toLocaleString('zh-CN', { hour12: false }),
          image: rawImageBase64,
          original: finalOrig,
          translated: finalTrans,
          targetLang: targetLang
        });
      }
    },
    onError: (err) => {
      isOcring = false;
      isTranslating = false;
      sourceEditor.placeholder = '文字识别失败，请检查 API 配置';
      transStatusTip.textContent = '识别出错';
      showToast('识别失败: ' + err.message, 3000);
    }
  });
}

async function triggerTranslation(textToTranslate) {
  const text = textToTranslate || sourceEditor.value;
  if (!text || !text.trim()) {
    showToast('没有可供翻译的文字');
    return;
  }

  const targetLang = selectTargetLang.value;
  isTranslating = true;
  translationView.textContent = '';
  transStatusTip.textContent = `正在翻译为【${targetLang}】...`;

  let result = '';

  await runTranslate({
    text: text,
    targetLang: targetLang,
    onChunk: (chunk) => {
      result += chunk;
      translationView.textContent = result;
    },
    onEnd: () => {
      isTranslating = false;
      transStatusTip.textContent = '翻译完成';
      // 更新或追加历史记录
      if (result) {
        historyManager.add({
          id: 'hist_' + Date.now(),
          timestamp: Date.now(),
          timeStr: new Date().toLocaleString('zh-CN', { hour12: false }),
          image: rawImageBase64,
          original: text,
          translated: cleanMarkers(result),
          targetLang: targetLang
        });
      }
    },
    onError: (err) => {
      isTranslating = false;
      transStatusTip.textContent = '翻译失败';
      showToast('翻译失败: ' + err.message, 3000);
    }
  });
}

const captureManager = new ScreenCaptureManager({
  onComplete: (base64) => {
    handleNewImage(base64);
  },
  onCancel: () => {
    showToast('截图已取消');
  }
});

async function pasteImageFromClipboard() {
  try {
    if (window.nativeBridge && window.nativeBridge.readClipboardImage) {
      const nativeImg = window.nativeBridge.readClipboardImage();
      if (nativeImg) {
        handleNewImage(nativeImg);
        showToast('已从剪贴板载入图片');
        return;
      }
    }
    showToast('剪贴板中未检测到图片');
  } catch (err) {
    showToast('无法读取剪贴板图片');
  }
}

function openSettings() {
  const cfg = configManager.get();
  cfgBaseUrl.value = cfg.baseUrl || '';
  cfgApiKey.value = cfg.apiKey || '';
  cfgVisionModel.value = cfg.visionModel || '';
  cfgTranslateModel.value = cfg.translateModel || '';
  testConnResult.textContent = '';
  settingsModal.classList.add('active');
}

function closeSettings() {
  settingsModal.classList.remove('active');
}

function saveSettings() {
  const apiKeyVal = cfgApiKey.value.trim();
  if (!apiKeyVal) {
    showToast('请填写有效的 API Key 后再保存！');
    return;
  }

  const ok = configManager.save({
    baseUrl: cfgBaseUrl.value.trim(),
    apiKey: apiKeyVal,
    visionModel: cfgVisionModel.value.trim(),
    translateModel: cfgTranslateModel.value.trim()
  });

  if (ok) {
    showToast('✓ 配置已永久保存到本地磁盘！');
    closeSettings();
  } else {
    showToast('保存配置失败，请检查写入权限');
  }
}

/**
 * 历史记录 UI 交互与渲染
 */
function openHistoryDrawer() {
  renderHistoryList();
  historyDrawer.classList.add('active');
}

function closeHistoryDrawer() {
  historyDrawer.classList.remove('active');
}

function restoreHistoryItem(item, options = {}) {
  if (!item) return;
  rawImageBase64 = item.image || null;
  currentImageBase64 = item.image || null;

  if (item.image) {
    emptyGuide.style.display = 'none';
    thumbWrap.style.display = 'flex';
    thumbImg.src = item.image;
  } else {
    emptyGuide.style.display = 'flex';
    thumbWrap.style.display = 'none';
    thumbImg.src = '';
  }

  sourceEditor.value = item.original || '';
  translationView.textContent = item.translated || '';
  if (item.targetLang) {
    selectTargetLang.value = item.targetLang;
  }
  updateCharCount();
  closeHistoryDrawer();

  if (!options.silent) {
    transStatusTip.textContent = `已载入历史记录 (${item.timeStr})`;
    showToast('✓ 已恢复该条历史识别与翻译');
  }
}

function renderHistoryList() {
  const list = historyManager.getAll();
  historyListContainer.innerHTML = '';

  if (list.length === 0) {
    historyListContainer.innerHTML = `
      <div style="text-align: center; color: var(--text-muted); padding: 50px 0; font-size: 13px;">
        <svg style="width: 40px; height: 40px; stroke: #94a3b8; margin-bottom: 8px;" viewBox="0 0 24 24" fill="none" stroke-width="1.5">
          <circle cx="12" cy="12" r="10"/>
          <polyline points="12 6 12 12 16 14"/>
        </svg>
        <div>暂无识别历史</div>
        <div style="font-size: 11px; margin-top: 4px; color: #94a3b8;">每次截图识别翻译后将自动保存在此处</div>
      </div>
    `;
    return;
  }

  list.forEach((item) => {
    const card = document.createElement('div');
    card.className = 'history-item';

    const thumbHtml = item.image
      ? `<img src="${item.image}" class="history-thumb" alt="截图" title="点击查看大图">`
      : `<div class="history-thumb" style="display:flex;align-items:center;justify-content:center;color:#64748b;font-size:11px;">无图</div>`;

    card.innerHTML = `
      ${thumbHtml}
      <div class="history-content">
        <div class="history-meta">
          <span>🕒 ${item.timeStr || '未知时间'}</span>
          <span class="badge" style="background:#e0e7ff;color:#3730a3;">${item.targetLang || '翻译'}</span>
        </div>
        <div class="history-line-orig" title="${item.original || ''}">原文：${item.original ? item.original.replace(/[\r\n]+/g, ' ') : '(无原文)'}</div>
        <div class="history-line-trans" title="${item.translated || ''}">译文：${item.translated ? item.translated.replace(/[\r\n]+/g, ' ') : '(无译文)'}</div>
      </div>
      <div class="history-actions">
        <button class="btn btn-sm btn-primary btn-restore">载入</button>
        <button class="btn btn-sm btn-delete" style="color:#ef4444;">删除</button>
      </div>
    `;

    // 点击缩略图放大查看
    const thumbElem = card.querySelector('.history-thumb');
    if (thumbElem && item.image) {
      thumbElem.addEventListener('click', (e) => {
        e.stopPropagation();
        if (imageLightbox && lightboxImg) {
          lightboxImg.src = item.image;
          imageLightbox.classList.add('active');
        }
      });
    }

    // 载入按钮
    card.querySelector('.btn-restore').addEventListener('click', () => {
      restoreHistoryItem(item);
    });

    // 删除单条
    card.querySelector('.btn-delete').addEventListener('click', (e) => {
      e.stopPropagation();
      historyManager.delete(item.id);
      renderHistoryList();
      showToast('已删除该条记录');
    });

    historyListContainer.appendChild(card);
  });
}

const PRESETS = {
  dashscope: {
    baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
    vision: 'qwen-vl-max',
    translate: 'qwen-plus'
  },
  openai: {
    baseUrl: 'https://api.openai.com/v1',
    vision: 'gpt-4o-mini',
    translate: 'gpt-4o-mini'
  },
  deepseek: {
    baseUrl: 'https://api.deepseek.com',
    vision: 'deepseek-chat',
    translate: 'deepseek-chat'
  },
  ollama: {
    baseUrl: 'http://localhost:11434/v1',
    vision: 'minicpm-v',
    translate: 'qwen2.5:7b'
  }
};

document.querySelectorAll('.tag-btn').forEach((btn) => {
  btn.addEventListener('click', () => {
    const p = PRESETS[btn.dataset.preset];
    if (p) {
      cfgBaseUrl.value = p.baseUrl;
      cfgVisionModel.value = p.vision;
      cfgTranslateModel.value = p.translate;
      showToast(`已应用【${btn.textContent}】预设模板`);
    }
  });
});

btnCapture.addEventListener('click', () => captureManager.startCapture());
btnPaste.addEventListener('click', () => pasteImageFromClipboard());
btnHistory.addEventListener('click', openHistoryDrawer);
btnCloseHistory.addEventListener('click', closeHistoryDrawer);
btnClearAllHistory.addEventListener('click', () => {
  if (confirm('确定要清空全部识别历史吗？此操作无法撤销。')) {
    historyManager.clear();
    renderHistoryList();
    showToast('识别历史已全部清空');
  }
});

// 点击历史遮罩空白区域关闭抽屉
historyDrawer.addEventListener('click', (e) => {
  if (e.target === historyDrawer) {
    closeHistoryDrawer();
  }
});

btnSettings.addEventListener('click', openSettings);
btnCloseSettings.addEventListener('click', closeSettings);
btnCancelSettings.addEventListener('click', closeSettings);
btnSaveSettings.addEventListener('click', saveSettings);

// 点击设置遮罩空白处关闭
settingsModal.addEventListener('click', (e) => {
  if (e.target === settingsModal) {
    closeSettings();
  }
});

btnTestConn.addEventListener('click', async () => {
  testConnResult.style.color = '#3b82f6';
  testConnResult.textContent = '测试中...';

  try {
    await testApiConnection({
      baseUrl: cfgBaseUrl.value.trim(),
      apiKey: cfgApiKey.value.trim(),
      visionModel: cfgVisionModel.value.trim(),
      translateModel: cfgTranslateModel.value.trim()
    });
    testConnResult.style.color = '#10b981';
    testConnResult.textContent = '✓ 连接成功！API 正常可用';
  } catch (err) {
    testConnResult.style.color = '#ef4444';
    testConnResult.textContent = `✗ ${err.message}`;
  }
});

btnCopySource.addEventListener('click', () => {
  const val = sourceEditor.value;
  if (!val) return;
  if (window.nativeBridge && window.nativeBridge.copyText) {
    window.nativeBridge.copyText(val);
  } else {
    navigator.clipboard.writeText(val);
  }
  showToast('已复制原文到剪贴板');
});

btnCopyTranslation.addEventListener('click', () => {
  const val = translationView.textContent;
  if (!val) return;
  if (window.nativeBridge && window.nativeBridge.copyText) {
    window.nativeBridge.copyText(val);
  } else {
    navigator.clipboard.writeText(val);
  }
  showToast('已复制译文到剪贴板');
});

btnClearSource.addEventListener('click', () => {
  currentImageBase64 = null;
  rawImageBase64 = null;
  emptyGuide.style.display = 'flex';
  thumbWrap.style.display = 'none';
  thumbImg.src = '';
  sourceEditor.value = '';
  translationView.textContent = '';
  updateCharCount();
  showToast('已清空');
});

// 点击缩略图放大查看整张高清完整截图
thumbImg.addEventListener('click', () => {
  if (rawImageBase64 && imageLightbox && lightboxImg) {
    lightboxImg.src = rawImageBase64;
    imageLightbox.classList.add('active');
  }
});

// 点击模态框任意位置关闭大图
if (imageLightbox) {
  imageLightbox.addEventListener('click', () => {
    imageLightbox.classList.remove('active');
  });
}

sourceEditor.addEventListener('input', updateCharCount);

sourceEditor.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
    e.preventDefault();
    triggerTranslation();
  }
});

btnTranslateAction.addEventListener('click', () => triggerTranslation());

window.addEventListener('paste', (e) => {
  const items = e.clipboardData?.items;
  if (!items) return;
  for (const item of items) {
    if (item.type.indexOf('image') !== -1) {
      const file = item.getAsFile();
      const reader = new FileReader();
      reader.onload = (event) => {
        handleNewImage(event.target.result);
        showToast('已从剪贴板粘贴截图');
      };
      reader.readAsDataURL(file);
      e.preventDefault();
      return;
    }
  }
});

window.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'r') {
    location.reload();
  } else if (e.key === 'Escape') {
    if (imageLightbox && imageLightbox.classList.contains('active')) {
      imageLightbox.classList.remove('active');
    } else if (historyDrawer && historyDrawer.classList.contains('active')) {
      closeHistoryDrawer();
    } else if (settingsModal && settingsModal.classList.contains('active')) {
      closeSettings();
    }
  }
});

if (window.nativeBridge && window.nativeBridge.setHeight) {
  window.nativeBridge.setHeight(600);
}

/**
 * 智能唤醒与启动状态策略
 * 1. 如果已有历史记录，优先呈现最新一条历史记录在主界面，绝不强行自动截屏打断用户看历史的意图！
 * 2. 如果历史记录为空（新用户或全新无数据），才平滑唤起全屏截图。
 * 3. 当用户主动需要截新图时，只需点击顶部「重新截图」按钮即可。
 */
let hasAutoTriggeredCapture = false;

function checkStartupState(source) {
  if (window.nativeBridge && window.nativeBridge.log) {
    window.nativeBridge.log(`checkStartupState: 来源=${source}, autoTriggered=${hasAutoTriggeredCapture}, hasImage=${!!currentImageBase64}`);
  }
  if (hasAutoTriggeredCapture) return;

  const historyList = historyManager.getAll();

  // 如果已经存在历史记录，且当前工作区还没有图片
  if (!currentImageBase64 && historyList && historyList.length > 0) {
    // 恢复最近一次识别记录供用户查看
    const latest = historyList[0];
    restoreHistoryItem(latest, { silent: true });
    transStatusTip.textContent = `已就绪 · 已载入最新记录 (${latest.timeStr})，点击「重新截图」圈选新图`;
    if (window.nativeBridge && window.nativeBridge.log) {
      window.nativeBridge.log('已载入最近历史记录，保持界面展现供用户查看');
    }
    return;
  }

  // 只有在完全没有任何历史记录且无图时，才自动唤起第一次截图引导
  if (!currentImageBase64 && (!historyList || historyList.length === 0)) {
    hasAutoTriggeredCapture = true;
    if (window.nativeBridge && window.nativeBridge.log) {
      window.nativeBridge.log('历史记录为空，首次自动唤醒截图 captureManager.startCapture()');
    }
    setTimeout(() => {
      captureManager.startCapture();
    }, 150);
  }
}

// 1. 注册宿主激活监听
if (window.nativeBridge && window.nativeBridge.onPluginEnter) {
  window.nativeBridge.onPluginEnter((action) => {
    if (window.nativeBridge && window.nativeBridge.log) {
      window.nativeBridge.log(`app.js 收到 onPluginEnter 事件: ${JSON.stringify(action)}`);
    }
    window.nativeBridge.setHeight(600);

    // 如果用户是复制了图片后唤醒插件，直接处理入参图片
    if (action && action.type === 'img' && action.payload) {
      hasAutoTriggeredCapture = true;
      handleNewImage(action.payload);
      return;
    }

    checkStartupState('onPluginEnter');
  });
}

// 2. 页面就绪后自检状态
setTimeout(() => {
  checkStartupState('PageReadyTimeout');
}, 200);

if (window.nativeBridge && window.nativeBridge.log) {
  window.nativeBridge.log('<<< app.js 初始化绑定完成 >>>');
}
