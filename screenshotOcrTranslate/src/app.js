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

  const fastPrompt = `请仔细识别图片中的文字，并将其精准翻译为【${targetLang}】。
严格遵守以下标记格式输出，严禁输出任何多余的开场白或解释：
<<<ORIGINAL>>>
(在此输出识别提取出的原文，保持原有换行格式)
<<<TRANSLATED>>>
(在此输出翻译后的${targetLang}译文)`;

  const payload = {
    model: config.visionModel || 'qwen-vl-max',
    stream: true,
    messages: [
      {
        role: 'user',
        content: [
          {
            type: 'text',
            text: fastPrompt
          },
          {
            type: 'image_url',
            image_url: {
              url: base64Image
            }
          }
        ]
      }
    ]
  };

  let mode = 'pre';
  let rawBuffer = '';

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
      rawBuffer += delta;

      if (mode === 'pre') {
        const origIdx = rawBuffer.indexOf('<<<ORIGINAL>>>');
        if (origIdx !== -1) {
          mode = 'original';
          rawBuffer = rawBuffer.substring(origIdx + '<<<ORIGINAL>>>'.length).replace(/^\s+/, '');
          if (rawBuffer) onOriginalChunk(rawBuffer);
        }
      } else if (mode === 'original') {
        const transIdx = rawBuffer.indexOf('<<<TRANSLATED>>>');
        if (transIdx !== -1) {
          mode = 'translated';
          rawBuffer = rawBuffer.substring(transIdx + '<<<TRANSLATED>>>'.length).replace(/^\s+/, '');
          if (rawBuffer) onTranslateChunk(rawBuffer);
        } else {
          onOriginalChunk(delta);
        }
      } else if (mode === 'translated') {
        onTranslateChunk(delta);
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
 * 3. 前端交互与状态管理
 */
const btnCapture = document.getElementById('btnCapture');
const btnPaste = document.getElementById('btnPaste');
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

const cfgBaseUrl = document.getElementById('cfgBaseUrl');
const cfgApiKey = document.getElementById('cfgApiKey');
const cfgVisionModel = document.getElementById('cfgVisionModel');
const cfgTranslateModel = document.getElementById('cfgTranslateModel');
const toastBar = document.getElementById('toastBar');

let currentImageBase64 = null;
let isTranslating = false;
let isOcring = false;

function showToast(msg, duration = 2000) {
  toastBar.textContent = msg;
  toastBar.classList.add('show');
  setTimeout(() => toastBar.classList.remove('show'), duration);
}

function updateCharCount() {
  const len = sourceEditor.value.length;
  sourceCharCount.textContent = `${len} 字`;
}

function compressImageIfNeeded(base64Data, maxWidth = 1200, maxHeight = 800) {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => {
      let width = img.naturalWidth || img.width;
      let height = img.naturalHeight || img.height;
      if (width <= maxWidth && height <= maxHeight) {
        resolve(base64Data);
        return;
      }
      const ratio = Math.min(maxWidth / width, maxHeight / height);
      const targetW = Math.round(width * ratio);
      const targetH = Math.round(height * ratio);

      const canvas = document.createElement('canvas');
      canvas.width = targetW;
      canvas.height = targetH;
      const ctx = canvas.getContext('2d');
      ctx.drawImage(img, 0, 0, targetW, targetH);
      const compressed = canvas.toDataURL('image/jpeg', 0.85);
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
  const base64Data = await compressImageIfNeeded(rawBase64);
  currentImageBase64 = base64Data;

  emptyGuide.style.display = 'none';
  thumbWrap.style.display = 'flex';
  thumbImg.src = base64Data;
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

  let originalBuffer = '';
  let translateBuffer = '';

  await runFastVisionOcrAndTranslate({
    base64Image: base64Data,
    targetLang: targetLang,
    onOriginalChunk: (chunk) => {
      originalBuffer += chunk;
      sourceEditor.value = originalBuffer;
      updateCharCount();
    },
    onTranslateChunk: (chunk) => {
      translateBuffer += chunk;
      translationView.textContent = translateBuffer;
      transStatusTip.textContent = `翻译中...`;
    },
    onEnd: () => {
      isOcring = false;
      isTranslating = false;
      sourceEditor.placeholder = '你可以直接在此自由修改、校对文字 (按 Ctrl+Enter 重译)...';
      transStatusTip.textContent = '翻译完成';
      showToast('✓ 提取与翻译完成！');
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
btnSettings.addEventListener('click', openSettings);
btnCloseSettings.addEventListener('click', closeSettings);
btnCancelSettings.addEventListener('click', closeSettings);
btnSaveSettings.addEventListener('click', saveSettings);

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
  emptyGuide.style.display = 'flex';
  thumbWrap.style.display = 'none';
  thumbImg.src = '';
  sourceEditor.value = '';
  translationView.textContent = '';
  updateCharCount();
  showToast('已清空');
});

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
  }
});

if (window.nativeBridge && window.nativeBridge.setHeight) {
  window.nativeBridge.setHeight(600);
}

/**
 * 自动截屏状态机与宿主事件监听
 */
let hasAutoTriggeredCapture = false;

function triggerAutoCaptureIfEmpty(source) {
  if (window.nativeBridge && window.nativeBridge.log) {
    window.nativeBridge.log(`triggerAutoCaptureIfEmpty: 来源=${source}, autoTriggered=${hasAutoTriggeredCapture}, hasImage=${!!currentImageBase64}`);
  }
  if (hasAutoTriggeredCapture) return;
  if (!currentImageBase64) {
    hasAutoTriggeredCapture = true;
    if (window.nativeBridge && window.nativeBridge.log) {
      window.nativeBridge.log('>>> 立即触发 captureManager.startCapture() <<<');
    }
    setTimeout(() => {
      captureManager.startCapture();
    }, 120);
  }
}

// 1. 注册宿主激活监听
if (window.nativeBridge && window.nativeBridge.onPluginEnter) {
  window.nativeBridge.onPluginEnter((action) => {
    if (window.nativeBridge && window.nativeBridge.log) {
      window.nativeBridge.log(`app.js 收到 onPluginEnter 事件: ${JSON.stringify(action)}`);
    }
    window.nativeBridge.setHeight(600);

    if (action && action.type === 'img' && action.payload) {
      hasAutoTriggeredCapture = true;
      handleNewImage(action.payload);
      return;
    }

    triggerAutoCaptureIfEmpty('onPluginEnter');
  });
}

// 2. 页面就绪后兜底自检（无图自动唤醒截图）
setTimeout(() => {
  triggerAutoCaptureIfEmpty('PageReadyTimeout');
}, 200);

if (window.nativeBridge && window.nativeBridge.log) {
  window.nativeBridge.log('<<< app.js 初始化绑定完成 >>>');
}
