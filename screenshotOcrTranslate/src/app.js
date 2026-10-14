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
    this.currentIntent = 'auto'; // 'auto' | 'ai_edit' | 'ocr'
  }

  async startCapture(intent = 'auto') {
    this.currentIntent = intent;
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
          window.nativeBridge.log(`调用 window.nativeBridge.captureScreen() [intent=${intent}]`);
        }
        const base64Data = await window.nativeBridge.captureScreen();
        this.isCapturing = false;
        if (this.onComplete) {
          this.onComplete(base64Data, this.currentIntent);
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
  imageEditProvider: 'modelscope',
  imageEditApiKey: '',
  imageEditModel: 'Qwen/Qwen-Image-Edit',
  customOcrPrompt: '请仔细识别并提取这张图片中的所有文字。严格保持原有的段落排版和顺序。若含有代码或公式请完整保留。请直接输出提取出的文本内容，严禁输出任何问候语、说明、解释或附加符号。',
  customTranslatePrompt: '你是一位精通多国语言的高级专业翻译官。请将输入的内容准确、地道、优雅地翻译成目标语言，语言通顺，语义精准，保持原有换行格式，直接输出译文，不要包含任何多余解释。',
  afterCaptureAction: 'toolbar' // 'toolbar' | 'ai_edit' | 'ocr'
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
 *
 * 采用「空闲超时」而非「总时长超时」：只要还在持续吐字就永不打断，
 * 但若整整 2 分钟一个字节都没收到（网关静默丢连接、断网、被中间设备劫持），
 * 就地 abort，避免界面永远停在"翻译中"。
 */
const STREAM_IDLE_TIMEOUT_MS = 120000;

async function streamFetch(url, options, onChunk, onEnd, onError) {
  const controller = new AbortController();
  let idleTimer = null;
  const armIdleTimer = () => {
    clearTimeout(idleTimer);
    idleTimer = setTimeout(() => controller.abort(), STREAM_IDLE_TIMEOUT_MS);
  };
  const clearIdleTimer = () => clearTimeout(idleTimer);

  try {
    armIdleTimer();
    const response = await fetch(url, { ...options, signal: controller.signal });

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
      armIdleTimer(); // 每收到一段数据就续期，长时间无输出才会触发超时

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
            // 非标准/被截断的 SSE 分片属正常现象（如心跳、空行），
            // 解析失败就跳过这条，但仍记录日志便于排查网关异常格式。
            if (window.nativeBridge && window.nativeBridge.log) {
              window.nativeBridge.log(`跳过无法解析的 SSE 分片: ${jsonStr.slice(0, 120)}`);
            }
          }
        }
      }
    }

    onEnd && onEnd();
  } catch (err) {
    const aborted = err && err.name === 'AbortError';
    const finalErr = aborted
      ? new Error('请求超时：超过 2 分钟未收到模型响应，请检查网络或 API 网关')
      : err;
    if (window.nativeBridge && window.nativeBridge.log) {
      window.nativeBridge.log(`streamFetch 出错: ${finalErr.message}`);
    }
    onError && onError(finalErr);
  } finally {
    clearIdleTimer();
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

  // 连通性测试必须有超时：填错域名或被防火墙黑洞时 fetch 会长时间挂起，
  // 设置页会一直卡在"测试中"，用户以为程序死了。
  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), 15000);
  let response;
  try {
    response = await fetch(endpoint, {
      method: 'POST',
      signal: controller.signal,
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
  } catch (err) {
    if (err && err.name === 'AbortError') {
      throw new Error('连接超时（15 秒无响应），请检查 Base URL 与网络代理');
    }
    throw err;
  } finally {
    clearTimeout(timeoutId);
  }

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
const TWO_DAYS_MS = 2 * 24 * 60 * 60 * 1000; // 仅保留最近 2 天 (48小时)

// 历史记录专用图片规格：截图原图动辄数 MB，直接入库会让历史文件膨胀到上百 MB，
// 每次读盘/写盘都变慢。压到最长边 1280 的 JPEG 后单条约 100~250KB，
// 既显著瘦身，又保留足够清晰度供日后回看与重新识别。
const HISTORY_IMAGE_MAX_EDGE = 1280;
const HISTORY_IMAGE_QUALITY = 0.82;

/**
 * 把完整截图压缩为历史记录用的中等尺寸 JPEG，返回 Promise<dataUrl>。
 * 任何异常都退回原图，宁可多占空间也不能让历史条目丢图。
 */
function buildHistoryImage(dataUrl) {
  return new Promise((resolve) => {
    if (!dataUrl || typeof dataUrl !== 'string' || dataUrl.indexOf('data:image/') !== 0) {
      resolve(dataUrl || '');
      return;
    }
    const img = new Image();
    img.onload = () => {
      try {
        const scale = Math.min(1, HISTORY_IMAGE_MAX_EDGE / Math.max(img.naturalWidth || img.width, img.naturalHeight || img.height));
        const w = Math.max(1, Math.round((img.naturalWidth || img.width) * scale));
        const h = Math.max(1, Math.round((img.naturalHeight || img.height) * scale));

        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        const ctx = canvas.getContext('2d');
        ctx.fillStyle = '#FFFFFF'; // JPEG 无透明通道，先铺白底避免透明区变黑
        ctx.fillRect(0, 0, w, h);
        ctx.drawImage(img, 0, 0, w, h);

        const out = canvas.toDataURL('image/jpeg', HISTORY_IMAGE_QUALITY);
        canvas.width = 0;
        canvas.height = 0;
        resolve(out);
      } catch (e) {
        resolve(dataUrl);
      }
    };
    img.onerror = () => resolve(dataUrl);
    img.src = dataUrl;
  });
}

const historyManager = {
  getAll: () => {
    let list = [];
    if (window.nativeBridge && typeof window.nativeBridge.loadHistory === 'function') {
      const saved = window.nativeBridge.loadHistory();
      if (Array.isArray(saved)) list = saved;
    } else {
      try {
        const saved = localStorage.getItem('rubick_ai_ocr_history_v1');
        if (saved) list = JSON.parse(saved);
      } catch (e) {}
    }

    // 过滤超过 2 天的数据，自动清理过期历史
    const now = Date.now();
    const validList = list.filter((item) => {
      if (!item.timestamp) return false;
      return (now - item.timestamp) <= TWO_DAYS_MS;
    });

    if (validList.length !== list.length) {
      historyManager.saveAll(validList);
    }

    return validList;
  },

  saveAll: (list) => {
    if (window.nativeBridge && typeof window.nativeBridge.saveHistory === 'function') {
      window.nativeBridge.saveHistory(list);
    }
    // localStorage 只有约 5MB 配额：直接把带 base64 图片的完整列表塞进去，
    // 几屏截图就会写爆（QuotaExceededError 一直被静默吞掉，用户全然不知）。
    // 这里只把纯文本字段作为降级备份，图片以磁盘文件为准。
    try {
      const textOnly = list.map((h) => Object.assign({}, h, { image: '' }));
      localStorage.setItem('rubick_ai_ocr_history_v1', JSON.stringify(textOnly));
    } catch (e) {
      if (window.nativeBridge && window.nativeBridge.log) {
        window.nativeBridge.log('localStorage 历史备份失败: ' + e.message);
      }
    }
  },

  add: async (item) => {
    // 入库前先把原图压成中等尺寸 JPEG（详情见 buildHistoryImage）
    const slimImage = await buildHistoryImage(item.image);
    // 关键：必须在 await 之后再读取当前列表。若先读列表再压缩，连续两条记录
    // 会各自基于同一份陈旧列表写盘，后完成的那条会把前一条覆盖掉。
    const list = historyManager.getAll();
    const filtered = list.filter((h) => h.id !== item.id);
    filtered.unshift(Object.assign({}, item, { image: slimImage }));
    const trimmed = filtered.slice(0, 50); // 保留最近 2 天内记录
    historyManager.saveAll(trimmed);
  },

  delete: (id) => {
    const list = historyManager.getAll();
    const updated = list.filter((h) => h.id !== id);
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
const btnToggleParallel = document.getElementById('btnToggleParallel');

const sourceEditor = document.getElementById('sourceEditor');
const sourceCharCount = document.getElementById('sourceCharCount');
const translationView = document.getElementById('translationView');
const parallelView = document.getElementById('parallelView');
const selectTargetLang = document.getElementById('selectTargetLang');
const transStatusTip = document.getElementById('transStatusTip');

// 逐段对照模式开关。默认关闭，保持原有的"流式纯译文"体验；
// 用户点「逐段对照」后，右侧切换为原文/译文段落一一对应的可编辑视图。
let isParallelMode = false;

// 对照视图的段落对应关系。译文被切段/过滤空白后再拼回字符串会丢失"第几段"的信息，
// 因此在内存里保留一份 [ {orig, trans} ]；为 null 表示需要从当前原文/译文重新推导。
let parallelRows = null;

const thumbWrap = document.getElementById('thumbWrap');
const thumbImg = document.getElementById('thumbImg');
const emptyGuide = document.getElementById('emptyGuide');

// 快捷操作工具栏与大预览卡片 DOM 元素
const captureActionBar = document.getElementById('captureActionBar');
const btnDismissAction = document.getElementById('btnDismissAction');
const actAiEdit = document.getElementById('actAiEdit');
const actOcr = document.getElementById('actOcr');
const actPin = document.getElementById('actPin');
const actCopy = document.getElementById('actCopy');
const actSave = document.getElementById('actSave');
const actionPreviewImg = document.getElementById('actionPreviewImg');
const actionPreviewStage = document.getElementById('actionPreviewStage');

// 缩略图迷你快捷操作按钮
const btnThumbOcr = document.getElementById('btnThumbOcr');
const btnThumbPin = document.getElementById('btnThumbPin');
const btnThumbCopy = document.getElementById('btnThumbCopy');
const btnThumbSave = document.getElementById('btnThumbSave');

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
const cfgAfterCaptureAction = document.getElementById('cfgAfterCaptureAction');
const toastBar = document.getElementById('toastBar');

let currentImageBase64 = null;
let rawImageBase64 = null;
let isTranslating = false;
let isOcring = false;

const imageLightbox = document.getElementById('imageLightbox');
const lightboxImg = document.getElementById('lightboxImg');

function showCaptureActionBar(dataUrl, bundle) {
  if (!captureActionBar) return;
  if (emptyGuide) emptyGuide.style.display = 'none';
  if (thumbWrap) thumbWrap.style.display = 'none';
  if (actionPreviewImg && dataUrl) actionPreviewImg.src = dataUrl;
  captureActionBar.style.display = 'flex';
  if (window.nativeBridge && window.nativeBridge.setHeight) {
    window.nativeBridge.setHeight(620);
  }
}

function hideCaptureActionBar() {
  if (captureActionBar) captureActionBar.style.display = 'none';
  if (currentImageBase64 || rawImageBase64) {
    if (thumbWrap) thumbWrap.style.display = 'flex';
  } else {
    if (emptyGuide) emptyGuide.style.display = 'flex';
  }
}

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
  // 识别任务进行中再次投喂新图，会让两条流式请求同时改写同一个编辑区与
  // isOcring/isTranslating 状态，最终文案互相覆盖。这里直接拒绝新的识别请求。
  if (isOcring) {
    showToast('正在识别上一张图片，请稍候…');
    return;
  }
  rawImageBase64 = rawBase64;
  const base64Data = await compressImageIfNeeded(rawBase64);
  currentImageBase64 = base64Data;

  emptyGuide.style.display = 'none';
  thumbWrap.style.display = 'flex';
  thumbImg.src = rawBase64; // 缩略图始终展示未经压缩的原生高清完整截图
  sourceEditor.value = '';
  translationView.textContent = '';
  // 新图进来时旧译文已清空，对照视图里的旧行必须一并清掉，
  // 否则会残留上一张图的段落，与左栏空原文错位对照；
  // 随后流式输出的过程本身也走纯译文视图，避免逐字重建 DOM 造成卡顿。
  beginStreamingDisplay();
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
      // 流式过程中对照视图是不刷新的（每来一个字就重建 DOM 会卡顿），
      // 统一在收尾时按最终定稿文本重建一次并切回对照视图。
      endStreamingDisplay();
      updateCharCount();
      isOcring = false;
      isTranslating = false;
      sourceEditor.placeholder = '你可以直接在此自由修改、校对文字 (按 Ctrl+Enter 重译)...';
      transStatusTip.textContent = '翻译完成';
      showToast('✓ 提取与翻译完成！');

      // 自动归档至历史记录 (最近 2 天)
      if (finalOrig || finalTrans) {
        historyManager.add({
          id: 'hist_' + Date.now(),
          timestamp: Date.now(),
          timeStr: new Date().toLocaleString('zh-CN', { hour12: false }),
          image: rawImageBase64,
          original: finalOrig,
          translated: finalTrans,
          targetLang: targetLang
        }).catch((e) => {
          if (window.nativeBridge && window.nativeBridge.log) {
            window.nativeBridge.log('归档历史记录失败: ' + e.message);
          }
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

  // 并发保护：连按 Ctrl+Enter 会在旧请求尚未结束时再发一条流式请求，
  // 两条流同时往 translationView 追加文本，译文必然错乱、历史也会重复入库。
  if (isTranslating || isOcring) {
    showToast('已有翻译/识别任务正在进行，请稍候');
    return;
  }

  const targetLang = selectTargetLang.value;
  isTranslating = true;
  translationView.textContent = '';
  beginStreamingDisplay();
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
      // 重译后译文整体变了，对照视图必须按新译文重建，否则右栏还显示上一版。
      endStreamingDisplay();
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
        }).catch((e) => {
          if (window.nativeBridge && window.nativeBridge.log) {
            window.nativeBridge.log('归档历史记录失败: ' + e.message);
          }
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

const cfgImageEditProvider = document.getElementById('cfgImageEditProvider');
const cfgImageEditApiKey = document.getElementById('cfgImageEditApiKey');
const cfgImageEditModel = document.getElementById('cfgImageEditModel');

let activeWorkspaceMode = 'ocr'; // 'ocr' | 'ai_edit'
let latestCaptureBundle = null;

function isMatchingCaptureBundle(bundle, targetDataUrl) {
  if (!bundle || !bundle.cleanDataUrl) return false;
  if (!targetDataUrl) return true;
  return bundle.annotatedDataUrl === targetDataUrl || bundle.cleanDataUrl === targetDataUrl;
}

function resolveAvailableBundle(fallbackDataUrl) {
  if (isMatchingCaptureBundle(latestCaptureBundle, fallbackDataUrl)) {
    return latestCaptureBundle;
  }
  if (window.nativeBridge && typeof window.nativeBridge.getLatestCaptureBundle === 'function') {
    const diskBundle = window.nativeBridge.getLatestCaptureBundle();
    if (isMatchingCaptureBundle(diskBundle, fallbackDataUrl)) {
      latestCaptureBundle = diskBundle;
      return diskBundle;
    }
  }
  if (fallbackDataUrl) {
    return {
      cleanDataUrl: fallbackDataUrl,
      annotatedDataUrl: fallbackDataUrl,
      maskDataUrl: null,
      hasMaskRegion: false,
      textNotes: [],
      boxes: []
    };
  }
  return null;
}

const captureManager = new ScreenCaptureManager({
  onComplete: (res, intent = 'auto') => {
    hasAutoTriggeredCapture = true;
    const base64Data = (res && res.dataUrl) ? res.dataUrl : (typeof res === 'string' ? res : null);
    if (!base64Data) return;

    if (res && res.bundle && res.bundle.cleanDataUrl) {
      latestCaptureBundle = res.bundle;
    } else {
      latestCaptureBundle = resolveAvailableBundle(base64Data);
    }

    rawImageBase64 = (latestCaptureBundle && latestCaptureBundle.cleanDataUrl) ? latestCaptureBundle.cleanDataUrl : base64Data;
    currentImageBase64 = rawImageBase64;

    // 贴图操作
    if (res && res.action === 'pin') {
      if (window.nativeBridge && window.nativeBridge.showMainWindow) {
        window.nativeBridge.showMainWindow();
      }
      showToast('✓ 已贴图到桌面 (Esc / 双击关闭贴图)');
      return;
    }

    // 取色操作
    if (res && res.action === 'copy_color') {
      showToast(`✓ 已取色并复制到剪贴板: ${res.color || ''}`);
      return;
    }

    // 仅复制
    if (res && res.action === 'copy') {
      showToast('✓ 截图已复制到系统剪贴板');
      return;
    }

    if (window.nativeBridge) {
      if (window.nativeBridge.showMainWindow) window.nativeBridge.showMainWindow();
      if (window.nativeBridge.setHeight) window.nativeBridge.setHeight(620);
    }

    // 1. 若显式意图为 AI 改图，或当前本来就在 AI 改图工作台，直接载入画板
    if (intent === 'ai_edit' || activeWorkspaceMode === 'ai_edit' || (res && res.action === 'ai_edit')) {
      switchWorkspaceMode('ai_edit');
      const b = latestCaptureBundle || { cleanDataUrl: base64Data, annotatedDataUrl: base64Data };
      aiEditController.loadBundle(b);
      showToast('✨ 已将截图载入 AI 智能改图画板，直接框选即可修改');
      return;
    }

    // 2. 若显式意图为 OCR，或在悬浮工具栏点击了【提取文字】，直接启动大模型识别
    if (intent === 'ocr' || (res && res.action === 'ocr')) {
      switchWorkspaceMode('ocr');
      hideCaptureActionBar();
      showToast('正在识别提取文字并翻译...');
      handleNewImage(base64Data);
      return;
    }

    // 3. 通用截图 (intent === 'auto')：根据用户偏好设置执行
    const cfg = configManager.get();
    const defaultAction = cfg.afterCaptureAction || 'toolbar';

    if (defaultAction === 'ai_edit') {
      switchWorkspaceMode('ai_edit');
      const b = latestCaptureBundle || { cleanDataUrl: base64Data, annotatedDataUrl: base64Data };
      aiEditController.loadBundle(b);
      showToast('✨ 已自动载入 AI 智能改图工作台');
      return;
    }

    if (defaultAction === 'ocr') {
      switchWorkspaceMode('ocr');
      hideCaptureActionBar();
      showToast('正在识别提取文字并翻译...');
      handleNewImage(base64Data);
      return;
    }

    // 默认行为：展示快捷操作工具栏与大图预览卡片，等待用户自主点选
    switchWorkspaceMode('ocr');
    showCaptureActionBar(base64Data, latestCaptureBundle);
    showToast('📸 截图已就绪，请在上方工具栏选择操作');
  },
  onCancel: () => {
    showToast('截图已取消');
  }
});

// 绑定快捷工具栏按钮事件
if (btnDismissAction) {
  btnDismissAction.addEventListener('click', () => {
    hideCaptureActionBar();
  });
}

if (actAiEdit) {
  actAiEdit.addEventListener('click', () => {
    hideCaptureActionBar();
    switchWorkspaceMode('ai_edit');
    const b = latestCaptureBundle || resolveAvailableBundle(rawImageBase64);
    if (b && b.cleanDataUrl) {
      aiEditController.loadBundle(b);
    } else if (rawImageBase64) {
      aiEditController.loadSingleImage(rawImageBase64);
    }
    showToast('✨ 已进入 AI 智能改图工作台');
  });
}

if (actOcr) {
  actOcr.addEventListener('click', () => {
    hideCaptureActionBar();
    const imgUrl = rawImageBase64 || currentImageBase64;
    if (imgUrl) {
      handleNewImage(imgUrl);
      showToast('正在识别提取文字并翻译...');
    }
  });
}

if (actPin) {
  actPin.addEventListener('click', () => {
    const imgUrl = rawImageBase64 || currentImageBase64;
    if (!imgUrl) return;
    if (window.nativeBridge && typeof window.nativeBridge.pinImageDataUrl === 'function') {
      const ok = window.nativeBridge.pinImageDataUrl(imgUrl);
      if (ok) {
        showToast('✓ 已钉在桌面上作为置顶贴图 (双击或Esc关闭)');
        return;
      }
    }
    showToast('当前环境不支持桌面贴图');
  });
}

if (actCopy) {
  actCopy.addEventListener('click', () => {
    const imgUrl = rawImageBase64 || currentImageBase64;
    if (!imgUrl) return;
    if (window.nativeBridge && typeof window.nativeBridge.copyImageDataUrl === 'function') {
      const ok = window.nativeBridge.copyImageDataUrl(imgUrl);
      if (ok) {
        showToast('✓ 截图已复制到系统剪贴板');
        return;
      }
    }
    showToast('复制图片失败');
  });
}

if (actSave) {
  actSave.addEventListener('click', () => {
    const imgUrl = rawImageBase64 || currentImageBase64;
    if (!imgUrl) return;
    const a = document.createElement('a');
    a.href = imgUrl;
    a.download = `Screenshot_${Date.now()}.png`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    showToast('💾 已保存高清截图到本地');
  });
}

if (actionPreviewStage) {
  actionPreviewStage.addEventListener('click', () => {
    const imgUrl = rawImageBase64 || currentImageBase64;
    if (imgUrl && lightboxImg && imageLightbox) {
      lightboxImg.src = imgUrl;
      imageLightbox.classList.add('active');
    }
  });
}

// 绑定收起态缩略图中的迷你操作按钮
if (btnThumbOcr) {
  btnThumbOcr.addEventListener('click', () => {
    const imgUrl = rawImageBase64 || currentImageBase64;
    if (imgUrl) {
      handleNewImage(imgUrl);
      showToast('重新提取文字中...');
    }
  });
}
if (btnThumbPin) {
  btnThumbPin.addEventListener('click', () => {
    const imgUrl = rawImageBase64 || currentImageBase64;
    if (imgUrl && window.nativeBridge && window.nativeBridge.pinImageDataUrl) {
      window.nativeBridge.pinImageDataUrl(imgUrl);
      showToast('✓ 已置顶贴图到桌面');
    }
  });
}
if (btnThumbCopy) {
  btnThumbCopy.addEventListener('click', () => {
    const imgUrl = rawImageBase64 || currentImageBase64;
    if (imgUrl && window.nativeBridge && window.nativeBridge.copyImageDataUrl) {
      window.nativeBridge.copyImageDataUrl(imgUrl);
      showToast('✓ 截图已复制到剪贴板');
    }
  });
}
if (btnThumbSave) {
  btnThumbSave.addEventListener('click', () => {
    const imgUrl = rawImageBase64 || currentImageBase64;
    if (!imgUrl) return;
    const a = document.createElement('a');
    a.href = imgUrl;
    a.download = `Screenshot_${Date.now()}.png`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    showToast('💾 已保存截图到本地');
  });
}

async function pasteImageFromClipboard() {
  try {
    if (window.nativeBridge && window.nativeBridge.readClipboardImage) {
      const nativeImg = window.nativeBridge.readClipboardImage();
      if (nativeImg) {
        hasAutoTriggeredCapture = true;
        if (activeWorkspaceMode === 'ai_edit') {
          // 仅当剪贴板图片恰好来自刚刚的 snipper.exe 截图（或 15 秒内的新鲜截图包）时才认领三图分离包
          const diskBundle = (window.nativeBridge && typeof window.nativeBridge.getLatestCaptureBundle === 'function')
            ? window.nativeBridge.getLatestCaptureBundle()
            : null;
          const isFreshSnipperClip = diskBundle && diskBundle.timestamp && (Date.now() - diskBundle.timestamp < 15000) && (
            diskBundle.annotatedDataUrl === nativeImg || diskBundle.cleanDataUrl === nativeImg
          );
          if (isFreshSnipperClip) {
            latestCaptureBundle = diskBundle;
            aiEditController.loadBundle(diskBundle);
            showToast('✓ 已载入最近截图的三图分离数据包（含红框定位）');
          } else {
            latestCaptureBundle = null;
            aiEditController.loadSingleImage(nativeImg);
            showToast('已从剪贴板载入图片至 AI 改图工作台');
          }
        } else {
          latestCaptureBundle = null;
          handleNewImage(nativeImg);
          showToast('已从剪贴板载入图片');
        }
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
  if (cfgImageEditProvider) cfgImageEditProvider.value = cfg.imageEditProvider || 'modelscope';
  if (cfgImageEditApiKey) cfgImageEditApiKey.value = cfg.imageEditApiKey || '';
  if (cfgImageEditModel) cfgImageEditModel.value = cfg.imageEditModel || 'Qwen/Qwen-Image-Edit';
  if (cfgAfterCaptureAction) cfgAfterCaptureAction.value = cfg.afterCaptureAction || 'toolbar';
  testConnResult.textContent = '';
  settingsModal.classList.add('active');
}

function closeSettings() {
  settingsModal.classList.remove('active');
}

function saveSettings() {
  const apiKeyVal = cfgApiKey.value.trim();
  const imgEditKeyVal = cfgImageEditApiKey ? cfgImageEditApiKey.value.trim() : '';
  if (!apiKeyVal && !imgEditKeyVal) {
    showToast('请填写有效的 API Key 后再保存！');
    return;
  }

  const prevCfg = configManager.get();
  const ok = configManager.save({
    ...prevCfg,
    baseUrl: cfgBaseUrl.value.trim(),
    apiKey: apiKeyVal,
    visionModel: cfgVisionModel.value.trim(),
    translateModel: cfgTranslateModel.value.trim(),
    imageEditProvider: cfgImageEditProvider ? cfgImageEditProvider.value : 'modelscope',
    imageEditApiKey: imgEditKeyVal,
    imageEditModel: cfgImageEditModel ? cfgImageEditModel.value.trim() : 'Qwen/Qwen-Image-Edit',
    afterCaptureAction: cfgAfterCaptureAction ? cfgAfterCaptureAction.value : 'toolbar'
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
  latestCaptureBundle = null;
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
  // 历史条目同样要驱动对照视图重建，否则在对照模式下点历史会看到一张空白右栏。
  parallelRows = null;
  if (isParallelMode) renderParallelView();
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

/**
 * 把整段文本切成段落：优先按空行切分；若文本来自截图、通篇没有空行，
 * 则退化为按单换行切分（截图 OCR 结果常常是一行一段）。
 */
function splitParagraphs(text) {
  if (!text) return [];
  const normalized = String(text).replace(/\r\n/g, '\n').trim();
  if (!normalized) return [];
  const byBlank = normalized.split(/\n{2,}/);
  const blocks = byBlank.length > 1 ? byBlank : normalized.split(/\n/);
  return blocks.map((p) => p.trim()).filter((p) => p.length > 0);
}

/**
 * 把对照视图里各段译文按顺序回写进 translationView（保持它是唯一数据源）。
 * 这样「复制译文」「归档历史」等既有逻辑完全不用改，取到的始终是编辑后的最新译文。
 */
function syncParallelToTranslation() {
  const rows = parallelView.querySelectorAll('.parallel-row');
  parallelRows = Array.from(rows).map((row) => ({
    orig: row.querySelector('.parallel-cell--orig').textContent,
    trans: row.querySelector('.parallel-cell--trans').textContent
  }));
  // 复制/归档用的纯文本里丢掉空段落（否则会粘出一串空白行），
  // 但对照视图本身走 parallelRows，段落对应关系不受这里过滤影响。
  translationView.textContent = parallelRows
    .map((r) => r.trans.trim())
    .filter((t) => t.length > 0)
    .join('\n\n');
}

/**
 * 依据当前原文与译文重建逐段对照视图。
 * 段落数不等时以多的一方为准，缺的一侧留空占位，方便用户对照补齐。
 */
function renderParallelView() {
  let rows = parallelRows;
  if (!rows) {
    const origParas = splitParagraphs(sourceEditor.value);
    const transParas = splitParagraphs(translationView.textContent);
    const rowCount = Math.max(origParas.length, transParas.length);
    rows = [];
    for (let i = 0; i < rowCount; i++) {
      rows.push({ orig: origParas[i] || '', trans: transParas[i] || '' });
    }
    parallelRows = rows;
  }

  parallelView.innerHTML = '';
  rows.forEach((r) => {
    const row = document.createElement('div');
    row.className = 'parallel-row';

    const origCell = document.createElement('div');
    origCell.className = 'parallel-cell parallel-cell--orig';
    origCell.textContent = r.orig;

    const transCell = document.createElement('div');
    transCell.className = 'parallel-cell parallel-cell--trans';
    transCell.contentEditable = 'true';
    transCell.spellcheck = false;
    transCell.textContent = r.trans;
    transCell.addEventListener('input', syncParallelToTranslation);

    row.appendChild(origCell);
    row.appendChild(transCell);
    parallelView.appendChild(row);
  });
}

/**
 * 左栏原文被手动校对时，只就地刷新对照视图左侧单元格。
 * 不整块重建 DOM：重建会按 translationView 重新推导段落，而译文里的空段落
 * 在拼接时已被过滤，重建会破坏已有的段落对应关系。
 */
function syncParallelToOrig() {
  const origParas = splitParagraphs(sourceEditor.value);
  if (!parallelRows || origParas.length !== parallelRows.length) {
    // 段落数变了（新增/删除空行）只能重建
    parallelRows = null;
    renderParallelView();
    return;
  }
  const cells = parallelView.querySelectorAll('.parallel-cell--orig');
  cells.forEach((cell, i) => {
    cell.textContent = origParas[i] || '';
    parallelRows[i].orig = origParas[i] || '';
  });
}

/**
 * 流式输出期间临时切回纯译文视图：逐段对照要等整段译文定稿才能切段，
 * 若一直停在旧对照上，用户会以为程序卡死。
 */
function beginStreamingDisplay() {
  parallelRows = null; // 新一轮译文即将覆盖，缓存的段落对应关系作废
  if (!isParallelMode) return;
  parallelView.style.display = 'none';
  translationView.style.display = '';
}

/**
 * 流式结束：按定稿文本重建对照并切回对照视图
 */
function endStreamingDisplay() {
  if (!isParallelMode) return;
  parallelRows = null;
  renderParallelView();
  translationView.style.display = 'none';
  parallelView.style.display = 'flex';
}

/**
 * 在「纯译文」与「逐段对照」两种右侧视图之间切换
 */
function applyParallelMode(enabled) {
  isParallelMode = enabled;
  if (enabled) {
    renderParallelView();
    translationView.style.display = 'none';
    parallelView.style.display = 'flex';
    btnToggleParallel.textContent = '纯译文';
    btnToggleParallel.title = '切回单纯的整段译文视图';
  } else {
    translationView.style.display = '';
    parallelView.style.display = 'none';
    btnToggleParallel.textContent = '逐段对照';
    btnToggleParallel.title = '原文与译文逐段左右对照，右侧译文可直接编辑';
  }
}

/**
 * HTML 转义。历史记录里的原文/译文均来自大模型输出，属于不可信内容；
 * 若不经转义直接拼进 innerHTML，一段形如 <img src=x onerror=...> 的识别结果
 * 就能在插件页面里执行任意脚本。
 */
function escapeHtml(value) {
  if (value === null || value === undefined) return '';
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
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
      ? `<img src="${escapeHtml(item.image)}" class="history-thumb" alt="截图" title="点击查看大图">`
      : `<div class="history-thumb" style="display:flex;align-items:center;justify-content:center;color:#64748b;font-size:11px;">无图</div>`;

    // 所有来自历史数据（模型输出 / 可被本地篡改）的字段一律先转义再拼接
    const origLine = item.original ? String(item.original).replace(/[\r\n]+/g, ' ') : '';
    const transLine = item.translated ? String(item.translated).replace(/[\r\n]+/g, ' ') : '';

    card.innerHTML = `
      ${thumbHtml}
      <div class="history-content">
        <div class="history-meta">
          <span>🕒 ${escapeHtml(item.timeStr || '未知时间')}</span>
          <span class="badge" style="background:#e0e7ff;color:#3730a3;">${escapeHtml(item.targetLang || '翻译')}</span>
        </div>
        <div class="history-line-orig" title="${escapeHtml(origLine)}">原文：${origLine ? escapeHtml(origLine) : '(无原文)'}</div>
        <div class="history-line-trans" title="${escapeHtml(transLine)}">译文：${transLine ? escapeHtml(transLine) : '(无译文)'}</div>
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

btnCapture.addEventListener('click', () => {
  const intent = activeWorkspaceMode === 'ai_edit' ? 'ai_edit' : 'auto';
  captureManager.startCapture(intent);
});
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

const btnTestImageEditConn = document.getElementById('btnTestImageEditConn');

async function testImageEditConnection({ provider, apiKey, model, baseUrl }) {
  if (!apiKey) {
    throw new Error('请先填写「重绘专用 Token / Key」（或填写上方主 API Key）');
  }

  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), 15000);

  try {
    if (provider === 'modelscope') {
      const res = await fetch('https://api-inference.modelscope.cn/v1/models', {
        method: 'GET',
        signal: controller.signal,
        headers: {
          Authorization: `Bearer ${apiKey}`
        }
      });
      if (!res.ok) {
        if (res.status === 401 || res.status === 403) {
          throw new Error('魔搭 Token 校验未通过 (401/403)，请检查 Token 是否正确且已在魔搭绑定阿里云账号');
        }
        throw new Error(`魔搭接口返回 HTTP ${res.status}`);
      }
      return `✓ 魔搭社区免费 API 连通成功！（当前模型: ${model || 'Qwen/Qwen-Image-Edit'}）`;
    }

    if (provider === 'siliconflow') {
      const res = await fetch('https://api.siliconflow.cn/v1/user/info', {
        method: 'GET',
        signal: controller.signal,
        headers: {
          Authorization: `Bearer ${apiKey}`
        }
      });
      if (!res.ok) {
        if (res.status === 401 || res.status === 403) {
          throw new Error('硅基流动 API Key 无效 (401)，请检查 sk- 开头的密钥是否正确');
        }
        throw new Error(`硅基流动接口返回 HTTP ${res.status}`);
      }
      const info = await res.json().catch(() => ({}));
      const bal = info?.data?.totalBalance;
      const balTip = bal !== undefined ? `，可用余额 ¥${bal}` : '';
      return `✓ 硅基流动连通成功${balTip}！（支持免费调用 ${model || 'deepseek-ai/Janus-Pro-7B'}）`;
    }

    if (provider === 'dashscope') {
      const res = await fetch('https://dashscope.aliyuncs.com/compatible-mode/v1/models', {
        method: 'GET',
        signal: controller.signal,
        headers: {
          Authorization: `Bearer ${apiKey}`
        }
      });
      if (!res.ok) {
        throw new Error(`阿里云百炼 Key 校验失败 (HTTP ${res.status})`);
      }
      return `✓ 阿里云百炼连通成功！（重绘模型: ${model || 'qwen-image-edit'}）`;
    }

    // 通用 OpenAI 兼容渠道
    const cleanBase = (baseUrl || 'https://api.openai.com/v1').replace(/\/+$/, '');
    const res = await fetch(`${cleanBase}/models`, {
      method: 'GET',
      signal: controller.signal,
      headers: {
        Authorization: `Bearer ${apiKey}`
      }
    });
    if (!res.ok) {
      throw new Error(`通用生图接口返回 HTTP ${res.status}`);
    }
    return `✓ 通用生图渠道连通成功！（模型: ${model || 'gpt-image-1'}）`;
  } catch (err) {
    if (err && err.name === 'AbortError') {
      throw new Error('重绘渠道连接超时（15 秒无响应），请检查网络');
    }
    throw err;
  } finally {
    clearTimeout(timeoutId);
  }
}

btnTestConn.addEventListener('click', async () => {
  testConnResult.style.color = '#3b82f6';
  testConnResult.textContent = '正在测试主 API...';

  try {
    await testApiConnection({
      baseUrl: cfgBaseUrl.value.trim(),
      apiKey: cfgApiKey.value.trim(),
      visionModel: cfgVisionModel.value.trim(),
      translateModel: cfgTranslateModel.value.trim()
    });
    testConnResult.style.color = '#10b981';
    testConnResult.textContent = '✓ 主 API 连接成功！OCR 与翻译正常可用';
  } catch (err) {
    testConnResult.style.color = '#ef4444';
    testConnResult.textContent = `✗ 主 API: ${err.message}`;
  }
});

if (btnTestImageEditConn) {
  btnTestImageEditConn.addEventListener('click', async () => {
    testConnResult.style.color = '#6d28d9';
    testConnResult.textContent = '正在测试重绘渠道 Token...';

    const provider = cfgImageEditProvider ? cfgImageEditProvider.value : 'modelscope';
    const rawEditKey = cfgImageEditApiKey ? cfgImageEditApiKey.value.trim() : '';
    const effectiveKey = rawEditKey || cfgApiKey.value.trim();
    const model = cfgImageEditModel ? cfgImageEditModel.value.trim() : '';

    try {
      const okMsg = await testImageEditConnection({
        provider,
        apiKey: effectiveKey,
        model,
        baseUrl: cfgBaseUrl.value.trim()
      });
      testConnResult.style.color = '#10b981';
      testConnResult.textContent = okMsg;
    } catch (err) {
      testConnResult.style.color = '#ef4444';
      testConnResult.textContent = `✗ 重绘渠道: ${err.message}`;
    }
  });
}

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

// 逐段对照 / 纯译文 两视图切换
if (btnToggleParallel) {
  btnToggleParallel.addEventListener('click', () => {
    applyParallelMode(!isParallelMode);
  });
}

btnClearSource.addEventListener('click', () => {
  currentImageBase64 = null;
  rawImageBase64 = null;
  emptyGuide.style.display = 'flex';
  thumbWrap.style.display = 'none';
  thumbImg.src = '';
  sourceEditor.value = '';
  translationView.textContent = '';
  // 清空时对照视图也要跟着清，否则会留下一屏没有原文对应的孤立译文行。
  parallelRows = null;
  if (isParallelMode) renderParallelView();
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

sourceEditor.addEventListener('input', () => {
  updateCharCount();
  // 对照模式下左栏原文是实时镜像，手动校对错字时它必须同步跟随；
  // 这里只就地改左侧单元格文本，纯译文模式下不会产生任何多余开销。
  if (isParallelMode) syncParallelToOrig();
});

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
        hasAutoTriggeredCapture = true;
        latestCaptureBundle = null;
        if (activeWorkspaceMode === 'ai_edit') {
          aiEditController.loadSingleImage(event.target.result);
          showToast('已粘贴截图至 AI 改图工作台');
        } else {
          handleNewImage(event.target.result);
          showToast('已从剪贴板粘贴截图');
        }
      };
      reader.readAsDataURL(file);
      e.preventDefault();
      return;
    }
  }
});

/**
 * ============================================================================
 * 4. 双工作区切换与分级双引擎 AI 智能改图控制器 (轻量级 VLM+Canvas / 重绘级 Image Edit)
 * ============================================================================
 */
const tabModeOcr = document.getElementById('tabModeOcr');
const tabModeAiEdit = document.getElementById('tabModeAiEdit');
const workspaceOcr = document.getElementById('workspaceOcr');
const workspaceAiEdit = document.getElementById('workspaceAiEdit');
const btnSendToAiEdit = document.getElementById('btnSendToAiEdit');

function switchWorkspaceMode(mode) {
  activeWorkspaceMode = mode === 'ai_edit' ? 'ai_edit' : 'ocr';
  if (activeWorkspaceMode === 'ai_edit') {
    if (tabModeOcr) tabModeOcr.classList.remove('active');
    if (tabModeAiEdit) tabModeAiEdit.classList.add('active');
    if (workspaceOcr) workspaceOcr.style.display = 'none';
    if (workspaceAiEdit) workspaceAiEdit.style.display = 'grid';
    // 若改图区尚无底图，优先尝试载入带红框分离数据的最新 bundle，其次回退 OCR 区截图
    if (!aiEditController.hasImage()) {
      const bundle = resolveAvailableBundle(rawImageBase64);
      if (bundle && bundle.cleanDataUrl) {
        aiEditController.loadBundle(bundle);
      }
    }
  } else {
    if (tabModeAiEdit) tabModeAiEdit.classList.remove('active');
    if (tabModeOcr) tabModeOcr.classList.add('active');
    if (workspaceAiEdit) workspaceAiEdit.style.display = 'none';
    if (workspaceOcr) workspaceOcr.style.display = 'grid';
  }
}

if (tabModeOcr) {
  tabModeOcr.addEventListener('click', () => switchWorkspaceMode('ocr'));
}
if (tabModeAiEdit) {
  tabModeAiEdit.addEventListener('click', () => switchWorkspaceMode('ai_edit'));
}
if (btnSendToAiEdit) {
  btnSendToAiEdit.addEventListener('click', () => {
    switchWorkspaceMode('ai_edit');
    const bundle = resolveAvailableBundle(rawImageBase64);
    if (bundle && bundle.cleanDataUrl) {
      aiEditController.loadBundle(bundle);
      showToast('已将当前截图载入 AI 智能改图工作台');
    }
  });
}

// 切换重绘渠道时自动给出推荐默认模型名称
if (cfgImageEditProvider && cfgImageEditModel) {
  cfgImageEditProvider.addEventListener('change', () => {
    const val = cfgImageEditProvider.value;
    const curModel = cfgImageEditModel.value.trim();
    const defaultSet = new Set(['', 'qwen-image-edit', 'Qwen/Qwen-Image-Edit', 'deepseek-ai/Janus-Pro-7B', 'Kwai-Kolors/Kolors']);
    if (val === 'modelscope' && defaultSet.has(curModel)) {
      cfgImageEditModel.value = 'Qwen/Qwen-Image-Edit';
    } else if (val === 'siliconflow' && defaultSet.has(curModel)) {
      cfgImageEditModel.value = 'deepseek-ai/Janus-Pro-7B';
    } else if (val === 'dashscope' && defaultSet.has(curModel)) {
      cfgImageEditModel.value = 'qwen-image-edit';
    }
  });
}

const aiEditController = (() => {
  // DOM 节点引用
  const aiEditMaskBadge = document.getElementById('aiEditMaskBadge');
  const aiEditThumbBar = document.getElementById('aiEditThumbBar');
  const aiEditAnnotatedThumb = document.getElementById('aiEditAnnotatedThumb');
  const aiEditBundleTitle = document.getElementById('aiEditBundleTitle');
  const aiEditBundleSub = document.getElementById('aiEditBundleSub');
  const tierCardLight = document.getElementById('tierCardLight');
  const tierCardHeavy = document.getElementById('tierCardHeavy');
  const aiPromptGroup = document.getElementById('aiPromptGroup');
  const aiPromptLabel = document.getElementById('aiPromptLabel');
  const lightAiPromptToggleWrap = document.getElementById('lightAiPromptToggleWrap');
  const btnToggleLightAiPrompt = document.getElementById('btnToggleLightAiPrompt');
  const aiEditPromptInput = document.getElementById('aiEditPromptInput');
  const aiPromptQuickTags = document.getElementById('aiPromptQuickTags');
  const lightLayerTuner = document.getElementById('lightLayerTuner');
  const selectActivePatch = document.getElementById('selectActivePatch');
  const btnDeletePatch = document.getElementById('btnDeletePatch');
  const tuneTextValue = document.getElementById('tuneTextValue');
  const tuneFillMode = document.getElementById('tuneFillMode');
  const tuneFontSize = document.getElementById('tuneFontSize');
  const tuneFontSizeLabel = document.getElementById('tuneFontSizeLabel');
  const tuneFontBold = document.getElementById('tuneFontBold');
  const tuneFontColor = document.getElementById('tuneFontColor');
  const tuneBgColor = document.getElementById('tuneBgColor');
  const tuneSmartErase = document.getElementById('tuneSmartErase');
  const lightFilterBox = document.getElementById('lightFilterBox');
  const btnResetFilters = document.getElementById('btnResetFilters');
  const rngBrightness = document.getElementById('rngBrightness');
  const rngContrast = document.getElementById('rngContrast');
  const rngSaturate = document.getElementById('rngSaturate');
  const rngWarmth = document.getElementById('rngWarmth');
  const valBrightness = document.getElementById('valBrightness');
  const valContrast = document.getElementById('valContrast');
  const valSaturate = document.getElementById('valSaturate');
  const valWarmth = document.getElementById('valWarmth');
  const heavyOptionsBox = document.getElementById('heavyOptionsBox');
  const heavyBoxStatusText = document.getElementById('heavyBoxStatusText');
  const btnClearHeavyBoxes = document.getElementById('btnClearHeavyBoxes');
  const chkLockNonMask = document.getElementById('chkLockNonMask');
  const aiTokenCostTip = document.getElementById('aiTokenCostTip');
  const btnAddManualPatch = document.getElementById('btnAddManualPatch');
  const btnRunAiEdit = document.getElementById('btnRunAiEdit');
  const btnRunAiEditText = document.getElementById('btnRunAiEditText');
  const aiEditStatusBadge = document.getElementById('aiEditStatusBadge');
  const btnHoldCompare = document.getElementById('btnHoldCompare');
  const btnUseAsBase = document.getElementById('btnUseAsBase');
  const aiEditEmptyGuide = document.getElementById('aiEditEmptyGuide');
  const aiCanvasWrap = document.getElementById('aiCanvasWrap');
  const aiEditCanvas = document.getElementById('aiEditCanvas');
  const aiEditLoadingOverlay = document.getElementById('aiEditLoadingOverlay');
  const aiEditLoadingText = document.getElementById('aiEditLoadingText');
  const aiEditFooterTip = document.getElementById('aiEditFooterTip');
  const btnAiSendOcr = document.getElementById('btnAiSendOcr');
  const btnAiPin = document.getElementById('btnAiPin');
  const btnAiSave = document.getElementById('btnAiSave');
  const btnAiCopy = document.getElementById('btnAiCopy');

  // 内部核心状态
  let currentTier = 'light'; // 'light' | 'heavy'
  let isLightAiPromptExpanded = false; // 轻量级下是否展开可选的 AI 自然语言输入框
  let pendingTuneText = ''; // 用户在尚未框选时预先在「替换文字」框内输入的文字
  let currentBundle = null;
  let cleanImgObj = null;
  let annotatedImgObj = null;
  let maskImgObj = null;
  let heavyResultImgObj = null; // 重绘级大模型返回的生成图

  // 离屏缓存干净原图像素，供边缘采样与无痕擦除使用
  const offscreenCleanCanvas = document.createElement('canvas');
  const offscreenCleanCtx = offscreenCleanCanvas.getContext('2d', { willReadFrequently: true });
  const fontMeasureCanvas = document.createElement('canvas');
  const fontMeasureCtx = fontMeasureCanvas.getContext('2d');

  // 轻量级修改图层列表
  let patches = [];
  let activePatchIndex = -1;
  let hoveredPatchIndex = -1;
  let lastConsumedPrompt = '';

  // 滤镜状态
  let filters = { brightness: 100, contrast: 100, saturate: 100, warmth: 0 };

  // 画布交互状态（拖拽文字位置 或 手动框选改字区）
  let isComparingOriginal = false;
  let isManualBoxMode = false;
  let isDraggingOnCanvas = false;
  let dragMode = 'none'; // 'move_patch' | 'draw_box'
  let dragStartPt = { x: 0, y: 0 };
  let dragInitialOffset = { x: 0, y: 0 };
  let tempManualBox = null;
  let isRunningAi = false;

  function loadImagePromise(src) {
    return new Promise((resolve, reject) => {
      if (!src) return resolve(null);
      const img = new Image();
      img.onload = () => resolve(img);
      img.onerror = (e) => reject(e);
      img.src = src;
    });
  }

  function rgbToHex(r, g, b) {
    const clamp = (v) => Math.max(0, Math.min(255, Math.round(v)));
    return '#' + [clamp(r), clamp(g), clamp(b)].map((x) => x.toString(16).padStart(2, '0')).join('');
  }

  function hexToRgb(hex) {
    const clean = String(hex || '#ffffff').replace('#', '');
    if (clean.length === 3) {
      return {
        r: parseInt(clean[0] + clean[0], 16) || 0,
        g: parseInt(clean[1] + clean[1], 16) || 0,
        b: parseInt(clean[2] + clean[2], 16) || 0
      };
    }
    return {
      r: parseInt(clean.slice(0, 2), 16) || 0,
      g: parseInt(clean.slice(2, 4), 16) || 0,
      b: parseInt(clean.slice(4, 6), 16) || 0
    };
  }

  /**
   * 鲁棒背景色众数聚类 + 像素级字形紧密包络检测 + 同行后文切片扫描算法：
   * 专门解决「只改一行字里的某几个字」以及「长字改短字出现大块留白」的四大难题：
   * 1) 剔除相邻文字笔画对背景采样的污染（杜绝深色背景擦出灰白色块）；
   * 2) 自动扫描真实文字笔画的上下左右像素边界 [glyphMinX, glyphMinY, glyphMaxX, glyphMaxY]，
   *    通过 measureText 反推与旁侧文字 1:1 严丝合缝的字号与垂直基线；
   * 3) 若未画红框（或框压到了半个字），自动向左右寻找字符间的垂直空白列，绝不切半个字；
   * 4) 自动扫描同一行右侧紧随的后半句像素切片 (lineTail)，当多字改少字时自动将右侧后文向左平移拼接，彻底消除行内空白！
   */
  function analyzeAndRefineTextBox(rx, ry, rw, rh, options = {}) {
    if (!cleanImgObj || offscreenCleanCanvas.width === 0) {
      return {
        x: Math.round(rx),
        y: Math.round(ry),
        width: Math.round(rw),
        height: Math.round(rh),
        bgColor: '#ffffff',
        fontColor: '#1e293b',
        bgIsUniform: true,
        fontSize: Math.max(12, Math.round(rh * 0.82)),
        isBold: false,
        lineTail: null
      };
    }

    const W = offscreenCleanCanvas.width;
    const H = offscreenCleanCanvas.height;
    const x0 = Math.max(0, Math.min(W - 2, Math.round(rx)));
    const y0 = Math.max(0, Math.min(H - 2, Math.round(ry)));
    const w0 = Math.max(4, Math.min(W - x0, Math.round(rw)));
    const h0 = Math.max(4, Math.min(H - y0, Math.round(rh)));

    // 1. 采集外围 3px 环形区域 + 框内全部像素，用 16x16x16 直方图找绝对占优的【纯净背景色众数】
    const pad = 4;
    const sx = Math.max(0, x0 - pad);
    const sy = Math.max(0, y0 - pad);
    const sw = Math.min(W - sx, w0 + pad * 2);
    const sh = Math.min(H - sy, h0 + pad * 2);
    const sampleData = offscreenCleanCtx.getImageData(sx, sy, sw, sh).data;

    const bins = new Map();
    const borderSamples = [];
    for (let py = 0; py < sh; py++) {
      for (let px = 0; px < sw; px++) {
        const isBorder = (px < pad || px >= sw - pad || py < pad || py >= sh - pad);
        const idx = (py * sw + px) * 4;
        const r = sampleData[idx], g = sampleData[idx + 1], b = sampleData[idx + 2];
        // 外圈像素权重翻倍（背景在外圈占比最高）
        const weight = isBorder ? 3 : 1;
        const key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
        let entry = bins.get(key);
        if (!entry) {
          entry = { count: 0, sr: 0, sg: 0, sb: 0 };
          bins.set(key, entry);
        }
        entry.count += weight;
        entry.sr += r * weight;
        entry.sg += g * weight;
        entry.sb += b * weight;
        if (isBorder) borderSamples.push({ r, g, b });
      }
    }

    let bestBin = null;
    for (const entry of bins.values()) {
      if (!bestBin || entry.count > bestBin.count) {
        bestBin = entry;
      }
    }
    const modeR = bestBin ? bestBin.sr / bestBin.count : 255;
    const modeG = bestBin ? bestBin.sg / bestBin.count : 255;
    const modeB = bestBin ? bestBin.sb / bestBin.count : 255;

    // 聚拢所有与众数色差 < 28 的像素求精确背景均值，彻底排除相邻文字笔画与红框的干扰
    let bgSumR = 0, bgSumG = 0, bgSumB = 0, bgCnt = 0;
    for (const sp of borderSamples) {
      const d = Math.abs(sp.r - modeR) + Math.abs(sp.g - modeG) + Math.abs(sp.b - modeB);
      if (d < 28) {
        bgSumR += sp.r;
        bgSumG += sp.g;
        bgSumB += sp.b;
        bgCnt++;
      }
    }
    const bgR = bgCnt > 0 ? bgSumR / bgCnt : modeR;
    const bgG = bgCnt > 0 ? bgSumG / bgCnt : modeG;
    const bgB = bgCnt > 0 ? bgSumB / bgCnt : modeB;
    // 若外圈 60% 以上像素都属于同一背景色簇，判定为纯色/均匀底色（直接用纯净底色无痕覆盖，绝不产生插值灰斑！）
    const bgIsUniform = borderSamples.length === 0 || (bgCnt / borderSamples.length >= 0.55);

    // 2. 在目标区域内扫描前景文字笔画像素（与背景色差 > 42）
    const boxData = offscreenCleanCtx.getImageData(x0, y0, w0, h0).data;
    const fgPixels = [];
    let fgMinX = w0, fgMaxX = -1, fgMinY = h0, fgMaxY = -1;

    for (let cy = 0; cy < h0; cy++) {
      for (let cx = 0; cx < w0; cx++) {
        const idx = (cy * w0 + cx) * 4;
        const r = boxData[idx], g = boxData[idx + 1], b = boxData[idx + 2];
        // 若用户粘贴的是已带红框的单张图，跳过纯红框像素 (R>190, G<70, B<70)，避免把红框当成文字颜色
        const isRedBoxStroke = (r > 190 && g < 75 && b < 75 && (r - g > 120));
        if (isRedBoxStroke) continue;

        const dist = Math.abs(r - bgR) + Math.abs(g - bgG) + Math.abs(b - bgB);
        if (dist > 45) {
          fgPixels.push({ r, g, b, dist });
          if (cx < fgMinX) fgMinX = cx;
          if (cx > fgMaxX) fgMaxX = cx;
          if (cy < fgMinY) fgMinY = cy;
          if (cy > fgMaxY) fgMaxY = cy;
        }
      }
    }

    // 计算原文字主色（取对比度最高的前 40% 笔画像素均值，消除抗锯齿半透明边缘导致的色彩发灰）
    let fgR = (bgR * 0.299 + bgG * 0.587 + bgB * 0.114) > 128 ? 30 : 242;
    let fgG = (bgR * 0.299 + bgG * 0.587 + bgB * 0.114) > 128 ? 41 : 244;
    let fgB = (bgR * 0.299 + bgG * 0.587 + bgB * 0.114) > 128 ? 59 : 248;

    if (fgPixels.length > 0) {
      fgPixels.sort((a, b) => b.dist - a.dist);
      const takeCount = Math.max(1, Math.min(60, Math.ceil(fgPixels.length * 0.4)));
      let sr = 0, sg = 0, sb = 0;
      for (let i = 0; i < takeCount; i++) {
        sr += fgPixels[i].r;
        sg += fgPixels[i].g;
        sb += fgPixels[i].b;
      }
      fgR = sr / takeCount;
      fgG = sg / takeCount;
      fgB = sb / takeCount;
    }

    // 3. 紧缩并对齐字形包络框（含 1D 垂直投影字符间隙智能切割、防误伤邻字、同行后文切片扫描逻辑）：
    let finalX = x0;
    let finalY = y0;
    let finalW = w0;
    let finalH = h0;
    let glyphX = x0 + 1;
    let glyphY = y0 + 1;
    let glyphW = Math.max(8, w0 - 2);
    let glyphH = Math.round(h0 * 0.78);
    let detectedBold = false;
    let hasRightNeighbor = false;
    let lineTail = null;

    if (fgMaxX >= fgMinX && fgMaxY >= fgMinY) {
      let absMinX = x0 + fgMinX;
      let absMaxX = x0 + fgMaxX;
      let absMinY = y0 + fgMinY;
      let absMaxY = y0 + fgMaxY;

      const isColHasFg = (testX, topY, botY) => {
        if (testX < 0 || testX >= W) return false;
        const colData = offscreenCleanCtx.getImageData(testX, topY, 1, botY - topY + 1).data;
        for (let i = 0; i < colData.length; i += 4) {
          const r = colData[i], g = colData[i + 1], b = colData[i + 2];
          if (r > 190 && g < 75 && b < 75 && (r - g > 120)) continue;
          if (Math.abs(r - bgR) + Math.abs(g - bgG) + Math.abs(b - bgB) > 45) return true;
        }
        return false;
      };

      // 3.1 防误伤邻字（向内退让）：
      if (absMaxX - absMinX >= 12) {
        if (absMaxX >= x0 + w0 - 2 && isColHasFg(x0 + w0, absMinY, absMaxY)) {
          const maxTrimR = Math.min(6, Math.floor((absMaxX - absMinX) * 0.25));
          for (let tr = 1; tr <= maxTrimR; tr++) {
            if (!isColHasFg(absMaxX - tr, absMinY, absMaxY)) {
              absMaxX = absMaxX - tr - 1;
              while (absMaxX > absMinX && !isColHasFg(absMaxX, absMinY, absMaxY)) absMaxX--;
              break;
            }
          }
        }
        if (absMinX <= x0 + 1 && isColHasFg(x0 - 1, absMinY, absMaxY)) {
          const maxTrimL = Math.min(6, Math.floor((absMaxX - absMinX) * 0.25));
          for (let tl = 1; tl <= maxTrimL; tl++) {
            if (!isColHasFg(absMinX + tl, absMinY, absMaxY)) {
              absMinX = absMinX + tl + 1;
              while (absMinX < absMaxX && !isColHasFg(absMinX, absMinY, absMaxY)) absMinX++;
              break;
            }
          }
        }
      }

      // 3.2 防残留半边字（向外吸附）：
      const maxExpandX = options.hasUserRedBox ? 7 : 16;
      let stepL = 0;
      while (stepL < maxExpandX && isColHasFg(absMinX - 1, absMinY, absMaxY)) {
        absMinX--;
        stepL++;
      }
      let stepR = 0;
      while (stepR < maxExpandX && isColHasFg(absMaxX + 1, absMinY, absMaxY)) {
        absMaxX++;
        stepR++;
      }

      glyphX = absMinX;
      glyphY = absMinY;
      glyphW = Math.max(8, absMaxX - absMinX + 1);
      glyphH = Math.max(8, absMaxY - absMinY + 1);

      // 估算笔画密度判定原字是否为粗体
      const strokeDensity = fgPixels.length / Math.max(1, glyphW * glyphH);
      detectedBold = strokeDensity > 0.42;

      // 3.3 安全抗锯齿擦除边距：仅当外侧 1px 列为空白背景时才向外扩 1px，绝不侵入紧凑排版的邻字！
      const padL = !isColHasFg(absMinX - 1, absMinY, absMaxY) ? (isColHasFg(absMinX - 2, absMinY, absMaxY) ? 1 : 2) : 0;
      const padR = !isColHasFg(absMaxX + 1, absMinY, absMaxY) ? (isColHasFg(absMaxX + 2, absMinY, absMaxY) ? 1 : 2) : 0;

      finalX = Math.max(0, absMinX - padL);
      finalY = Math.max(0, absMinY - 2);
      finalW = Math.min(W - finalX, glyphW + padL + padR);
      finalH = Math.min(H - finalY, glyphH + 4);

      // 3.4 检测右侧是否存在紧邻的同行后文，并提取同行后文像素切片 (lineTail)：
      // 这样当「框住的字很多、要改的字很少」时，右侧剩余的句子能自动向左平移贴合新文字，彻底消除句子中间的大块留白！
      const scanTopY = Math.max(0, absMinY - 2);
      const scanBotY = Math.min(H - 1, absMaxY + 2);
      const maxInitialGap = Math.max(14, Math.round(glyphH * 1.05));
      const maxInnerGap = Math.max(18, Math.round(glyphH * 1.25));

      const isColBgConsistent = (testX, topY, botY) => {
        if (testX < 0 || testX >= W) return false;
        const topPx = offscreenCleanCtx.getImageData(testX, Math.max(0, topY - 1), 1, 1).data;
        const botPx = offscreenCleanCtx.getImageData(testX, Math.min(H - 1, botY + 1), 1, 1).data;
        const dTop = Math.abs(topPx[0] - bgR) + Math.abs(topPx[1] - bgG) + Math.abs(topPx[2] - bgB);
        const dBot = Math.abs(botPx[0] - bgR) + Math.abs(botPx[1] - bgG) + Math.abs(botPx[2] - bgB);
        return dTop < 48 && dBot < 48;
      };

      let tailStartX = -1;
      let tailEndX = -1;
      let emptyRun = 0;
      for (let cx = absMaxX + 1; cx < W - 1; cx++) {
        if (!isColBgConsistent(cx, scanTopY, scanBotY)) break;
        if (isColHasFg(cx, scanTopY, scanBotY)) {
          if (tailStartX === -1) {
            if (emptyRun > maxInitialGap) break;
            tailStartX = cx;
          }
          tailEndX = cx;
          emptyRun = 0;
        } else {
          emptyRun++;
          if (tailStartX === -1 && emptyRun > maxInitialGap) break;
          if (tailStartX !== -1 && emptyRun > maxInnerGap) break;
        }
      }

      if (tailStartX !== -1 && tailEndX >= tailStartX) {
        hasRightNeighbor = true;
        const tailSliceY = Math.max(0, finalY - 1);
        const tailSliceBot = Math.min(H, finalY + finalH + 1);
        lineTail = {
          srcX: tailStartX,
          srcY: tailSliceY,
          width: Math.min(W - tailStartX, tailEndX - tailStartX + 2),
          height: Math.max(4, tailSliceBot - tailSliceY),
          initialGap: Math.max(1, Math.min(10, tailStartX - absMaxX - 1))
        };
      }
    }

    // 4. 使用 Canvas measureText 精确反推能渲染出相同像素字高 glyphH 的 fontSize
    const targetCharCount = options.newText ? String(options.newText).trim().length : 0;
    const origCharCount = options.originalText ? String(options.originalText).trim().length : 0;
    const sampleStr = (options.newText && String(options.newText).trim()) || '中';
    const weightStr = detectedBold ? 'bold' : 'normal';

    let bestFontSize = Math.max(10, Math.min(72, Math.round(glyphH * 1.05)));
    let minDiff = Infinity;
    for (let testFs = 9; testFs <= 72; testFs++) {
      fontMeasureCtx.font = `${weightStr} ${testFs}px "Microsoft YaHei UI", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
      const m = fontMeasureCtx.measureText(sampleStr);
      const actualH = (m.actualBoundingBoxAscent !== undefined && m.actualBoundingBoxDescent !== undefined)
        ? (m.actualBoundingBoxAscent + m.actualBoundingBoxDescent)
        : (testFs * 0.90);
      let diff = Math.abs(actualH - glyphH);
      // 若替换前后字数相同（如 2 个字换 2 个字：“右侧” -> “左侧”），同时兼顾总字宽与原字宽完全一致！
      if (targetCharCount > 0 && targetCharCount === origCharCount && glyphW > 8) {
        diff = diff * 0.65 + Math.abs(m.width - glyphW) * 0.35;
      }
      if (diff < minDiff) {
        minDiff = diff;
        bestFontSize = testFs;
      }
    }

    return {
      x: finalX,
      y: finalY,
      width: finalW,
      height: finalH,
      glyphX,
      glyphY,
      glyphW,
      glyphH,
      hasRightNeighbor,
      lineTail,
      bgColor: rgbToHex(bgR, bgG, bgB),
      fontColor: rgbToHex(fgR, fgG, fgB),
      bgIsUniform,
      fontSize: bestFontSize,
      isBold: detectedBold
    };
  }

  function sampleColorsFromCleanRect(rx, ry, rw, rh) {
    return analyzeAndRefineTextBox(rx, ry, rw, rh, { hasUserRedBox: true });
  }

  /**
   * 过滤出真正的闭合/涂抹区域标注框（排除 Text 文字说明、Arrow 箭头、Line 直线、Number 序号等指示性批注）
   */
  function getRegionTargetBoxes(boxes) {
    if (!Array.isArray(boxes)) return [];
    const regionTypes = new Set(['Rect', 'Ellipse', 'Marker', 'Brush', 'Mosaic', 'Blur']);
    const regions = boxes.filter((b) => regionTypes.has(b.type));
    if (regions.length > 0) return regions;
    return boxes.filter((b) => b.type !== 'Text' && b.type !== 'Arrow' && b.type !== 'Line' && b.type !== 'Number');
  }

  /**
   * 智能无痕擦除算法：
   * 1) 若局部背景属于均匀底色（如深色卡片 #141413、白底文档 #ffffff 等，占比 95% 以上），
   *    直接使用聚类提纯后的背景主色填充，100% 杜绝相邻汉字笔画被插值拉扯出的灰白脏块！
   * 2) 若局部背景存在明显渐变，则在做四向双线性插值前，先将四条采样边上的文字笔画像素清洗为背景主色，
   *    确保即使紧贴邻字擦除也绝不漏色、绝不发灰。
   */
  function smartInfillRectOnCtx(ctx, p) {
    const W = offscreenCleanCanvas.width;
    const H = offscreenCleanCanvas.height;
    const x = Math.max(0, Math.min(W - 1, Math.round(p.x)));
    const y = Math.max(0, Math.min(H - 1, Math.round(p.y)));
    const w = Math.max(1, Math.min(W - x, Math.round(p.width)));
    const h = Math.max(1, Math.min(H - y, Math.round(p.height)));
    if (w <= 1 || h <= 1) return;

    const bgRgb = hexToRgb(p.bgColor || '#ffffff');

    // 均匀底色直接填充纯净背景色，边缘零色差、内部零脏斑
    if (p.bgIsUniform !== false) {
      ctx.fillStyle = p.bgColor || '#ffffff';
      ctx.fillRect(x, y, w, h);
      return;
    }

    const patchData = ctx.createImageData(w, h);
    const dst = patchData.data;

    const topY = Math.max(0, y - 1);
    const botY = Math.min(H - 1, y + h);
    const leftX = Math.max(0, x - 1);
    const rightX = Math.min(W - 1, x + w);

    const topRow = offscreenCleanCtx.getImageData(x, topY, w, 1).data;
    const botRow = offscreenCleanCtx.getImageData(x, botY, w, 1).data;
    const leftCol = offscreenCleanCtx.getImageData(leftX, y, 1, h).data;
    const rightCol = offscreenCleanCtx.getImageData(rightX, y, 1, h).data;

    // 清洗边界采样数组中的邻字笔画噪点：凡偏离背景主色超过 36 的边界点，一律净化为背景主色
    const sanitizeEdge = (arr) => {
      for (let i = 0; i < arr.length; i += 4) {
        const d = Math.abs(arr[i] - bgRgb.r) + Math.abs(arr[i + 1] - bgRgb.g) + Math.abs(arr[i + 2] - bgRgb.b);
        if (d > 36) {
          arr[i] = bgRgb.r;
          arr[i + 1] = bgRgb.g;
          arr[i + 2] = bgRgb.b;
        }
      }
    };
    sanitizeEdge(topRow);
    sanitizeEdge(botRow);
    sanitizeEdge(leftCol);
    sanitizeEdge(rightCol);

    for (let py = 0; py < h; py++) {
      const wyTop = (h - py) / (h + 1);
      const wyBot = (py + 1) / (h + 1);
      const lIdx = py * 4;
      const rIdx = py * 4;

      for (let px = 0; px < w; px++) {
        const wxLeft = (w - px) / (w + 1);
        const wxRight = (px + 1) / (w + 1);
        const tIdx = px * 4;
        const bIdx = px * 4;

        const wTop = wyTop * 1.2;
        const wBot = wyBot * 1.2;
        const wLeft = wxLeft * 0.8;
        const wRight = wxRight * 0.8;
        const wSum = wTop + wBot + wLeft + wRight;

        const outIdx = (py * w + px) * 4;
        dst[outIdx] = Math.round((topRow[tIdx] * wTop + botRow[bIdx] * wBot + leftCol[lIdx] * wLeft + rightCol[rIdx] * wRight) / wSum);
        dst[outIdx + 1] = Math.round((topRow[tIdx + 1] * wTop + botRow[bIdx + 1] * wBot + leftCol[lIdx + 1] * wLeft + rightCol[rIdx + 1] * wRight) / wSum);
        dst[outIdx + 2] = Math.round((topRow[tIdx + 2] * wTop + botRow[bIdx + 2] * wBot + leftCol[lIdx + 2] * wLeft + rightCol[rIdx + 2] * wRight) / wSum);
        dst[outIdx + 3] = 255;
      }
    }

    ctx.putImageData(patchData, x, y);
  }

  /**
   * 核心画布渲染管线：按当前模式、滤镜、轻量级文字图层或重绘级羽化蒙版实时合成到 #aiEditCanvas
   */
  function renderCanvas(options = {}) {
    if (!cleanImgObj || !aiEditCanvas) return;
    const W = cleanImgObj.naturalWidth || cleanImgObj.width;
    const H = cleanImgObj.naturalHeight || cleanImgObj.height;
    if (aiEditCanvas.width !== W || aiEditCanvas.height !== H) {
      aiEditCanvas.width = W;
      aiEditCanvas.height = H;
    }

    const ctx = aiEditCanvas.getContext('2d');
    ctx.clearRect(0, 0, W, H);

    // 1. 按住对比原图时直接展示 100% 原始干净底图
    if (isComparingOriginal) {
      ctx.drawImage(cleanImgObj, 0, 0, W, H);
      return;
    }

    // 2. 应用全局色彩滤镜 (亮度/对比度/饱和度)
    const hasFilter = filters.brightness !== 100 || filters.contrast !== 100 || filters.saturate !== 100;
    ctx.save();
    if (hasFilter) {
      ctx.filter = `brightness(${filters.brightness}%) contrast(${filters.contrast}%) saturate(${filters.saturate}%)`;
    }

    // 3. 绘制底图或重绘级生成图
    if (heavyResultImgObj && currentTier === 'heavy') {
      const lockNonMask = chkLockNonMask && chkLockNonMask.checked && currentBundle && currentBundle.hasMaskRegion && maskImgObj;
      if (lockNonMask) {
        // 先画 100% 原画质干净原图
        ctx.drawImage(cleanImgObj, 0, 0, W, H);
        // 在离屏 Canvas 上将重绘结果与高斯羽化后的 Mask 蒙版相交，再无缝回贴到原图上
        const maskCompCanvas = document.createElement('canvas');
        maskCompCanvas.width = W;
        maskCompCanvas.height = H;
        const mCtx = maskCompCanvas.getContext('2d');

        // 转换黑白遮罩为 Alpha 遮罩并轻微羽化边缘 2px，兼顾小选区清晰度与边缘自然融合
        const featherCanvas = document.createElement('canvas');
        featherCanvas.width = W;
        featherCanvas.height = H;
        const fCtx = featherCanvas.getContext('2d');
        fCtx.filter = 'blur(2px)';
        fCtx.drawImage(maskImgObj, 0, 0, W, H);
        fCtx.filter = 'none';
        const fData = fCtx.getImageData(0, 0, W, H);
        const px = fData.data;
        for (let i = 0; i < px.length; i += 4) {
          // 白色区域(R值高)转为不透明 Alpha，黑色区域转为全透明
          px[i + 3] = px[i];
        }
        fCtx.putImageData(fData, 0, 0);

        mCtx.drawImage(heavyResultImgObj, 0, 0, W, H);
        mCtx.globalCompositeOperation = 'destination-in';
        mCtx.drawImage(featherCanvas, 0, 0, W, H);

        ctx.drawImage(maskCompCanvas, 0, 0, W, H);
      } else {
        ctx.drawImage(heavyResultImgObj, 0, 0, W, H);
      }
    } else {
      ctx.drawImage(cleanImgObj, 0, 0, W, H);
    }
    ctx.restore();

    // 4A. 仅在「⚡ 轻量级」模式下绘制无痕擦除与矢量文字图层（绝不在重绘级模式下擦除原图内容！）
    if (currentTier === 'light' && patches.length > 0) {
      patches.forEach((p, idx) => {
        const fillMode = p.fillMode || (p.align === 'center' ? 'center' : 'reflow');

        // 4.1 擦除原文字背景（若处于 reflow 后文自动拼接模式且存在同行右侧后文切片 lineTail，连同 lineTail 原位置一并无痕擦除，以便后续向左/右平移拼接）
        if (p.smartErase) {
          smartInfillRectOnCtx(ctx, p);
          if (fillMode === 'reflow' && p.lineTail && p.lineTail.width > 0) {
            smartInfillRectOnCtx(ctx, {
              x: Math.max(0, p.lineTail.srcX - 1),
              y: p.lineTail.srcY,
              width: p.lineTail.width + 2,
              height: p.lineTail.height,
              bgColor: p.bgColor,
              bgIsUniform: p.bgIsUniform
            });
          }
        } else {
          ctx.fillStyle = p.bgColor || '#ffffff';
          ctx.fillRect(p.x, p.y, p.width, p.height);
          if (fillMode === 'reflow' && p.lineTail && p.lineTail.width > 0) {
            ctx.fillRect(Math.max(0, p.lineTail.srcX - 1), p.lineTail.srcY, p.lineTail.width + 2, p.lineTail.height);
          }
        }

        const anchorX = (p.glyphX !== undefined ? p.glyphX : p.x + 1);
        const anchorY = (p.glyphY !== undefined ? p.glyphY : p.y + 2);
        const anchorW = (p.glyphW !== undefined ? p.glyphW : Math.max(6, p.width - 2));
        const anchorH = (p.glyphH !== undefined ? p.glyphH : Math.max(6, p.height - 4));
        let renderedTextW = 0;

        // 4.2 绘制替换后的新文字
        if (p.newText && String(p.newText).length > 0) {
          ctx.save();
          const weight = p.isBold ? 'bold' : 'normal';
          let fontSize = Math.max(8, Number(p.fontSize) || 16);
          ctx.font = `${weight} ${fontSize}px "Microsoft YaHei UI", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
          ctx.fillStyle = p.fontColor || '#1e293b';

          const lines = String(p.newText).split(/\r?\n/);
          if (lines.length === 1) {
            const lineStr = lines[0];

            // 若用户选择「🔍 字号自动缩放铺满 (scale)」，动态计算能撑满选区宽高且不越界的字号
            if (fillMode === 'scale') {
              for (let testFs = 8; testFs <= 96; testFs++) {
                ctx.font = `${weight} ${testFs}px "Microsoft YaHei UI", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
                const tm = ctx.measureText(lineStr);
                const th = (tm.actualBoundingBoxAscent !== undefined && tm.actualBoundingBoxDescent !== undefined)
                  ? (tm.actualBoundingBoxAscent + tm.actualBoundingBoxDescent)
                  : (testFs * 0.9);
                if (tm.width > anchorW * 0.96 || th > p.height * 0.92) {
                  fontSize = Math.max(8, testFs - 1);
                  break;
                }
                fontSize = testFs;
              }
              ctx.font = `${weight} ${fontSize}px "Microsoft YaHei UI", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
            }

            const m = ctx.measureText(lineStr);
            renderedTextW = m.width;

            const computeBaselineY = (metrics) => {
              if (metrics.actualBoundingBoxAscent !== undefined && metrics.actualBoundingBoxDescent !== undefined) {
                ctx.textBaseline = 'alphabetic';
                const newGlyphH = metrics.actualBoundingBoxAscent + metrics.actualBoundingBoxDescent;
                const topY = anchorY + (anchorH - newGlyphH) / 2 + (p.textYOffset || 0);
                return topY + metrics.actualBoundingBoxAscent;
              }
              ctx.textBaseline = 'middle';
              return anchorY + anchorH / 2 + (p.textYOffset || 0);
            };

            const baselineY = computeBaselineY(m);

            if (fillMode === 'spread' && lineStr.length > 1 && m.width < anchorW - 2) {
              // ↔️ 字距均匀拉开铺满原框（保持字高与旁侧文字 1:1 一致，自动拉开字符间距铺满原框宽度，消除右侧留白）
              ctx.textAlign = 'left';
              const chars = Array.from(lineStr);
              const charWidths = chars.map((ch) => ctx.measureText(ch).width);
              const totalCharW = charWidths.reduce((acc, w) => acc + w, 0);
              const stepGap = Math.max(0, (anchorW - totalCharW) / Math.max(1, chars.length - 1));
              let curX = anchorX + (p.textXOffset || 0);
              chars.forEach((ch, cIdx) => {
                ctx.fillText(ch, curX, baselineY);
                curX += charWidths[cIdx] + stepGap;
              });
              renderedTextW = anchorW;
            } else if (fillMode === 'center' || fillMode === 'scale') {
              // 🎯 原位居中对齐 / 字号自适应铺满居中
              ctx.textAlign = 'center';
              const drawX = anchorX + anchorW / 2 + (p.textXOffset || 0);
              ctx.fillText(lineStr, drawX, baselineY);
            } else {
              // 🔗 后文自动平移拼接 (reflow) 或 📐 原位左对齐 (left)
              ctx.textAlign = 'left';
              const drawX = anchorX + (p.textXOffset || 0);
              // 仅在非 reflow 且右侧有未平移邻字时才限制最大宽度；reflow 模式下右侧后文会自动向右让位或向左收拢，因此新文字保持 100% 自然字宽！
              const maxAllowedW = (fillMode !== 'reflow' && p.hasRightNeighbor && m.width > p.width - 1)
                ? Math.max(8, p.width - 1)
                : undefined;
              if (maxAllowedW !== undefined) {
                ctx.fillText(lineStr, drawX, baselineY, maxAllowedW);
                renderedTextW = Math.min(m.width, maxAllowedW);
              } else {
                ctx.fillText(lineStr, drawX, baselineY);
              }
            }
          } else {
            ctx.textBaseline = 'middle';
            const lineHeight = fontSize * 1.22;
            const totalTextH = lines.length * lineHeight;
            const startY = p.y + p.height / 2 - totalTextH / 2 + lineHeight / 2 + (p.textYOffset || 0);
            lines.forEach((line, lineIdx) => {
              let drawX = anchorX + (p.textXOffset || 0);
              if (fillMode === 'center') {
                ctx.textAlign = 'center';
                drawX = p.x + p.width / 2 + (p.textXOffset || 0);
              } else {
                ctx.textAlign = 'left';
              }
              ctx.fillText(line, drawX, startY + lineIdx * lineHeight);
            });
          }
          ctx.restore();
        }

        // 4.2.5 若启用了「🔗 后文自动平移拼接 (reflow)」且同行右侧存在后半句切片 lineTail，将其从干净底图无缝平移贴到新文字右侧！
        if (fillMode === 'reflow' && p.lineTail && p.lineTail.width > 0 && offscreenCleanCanvas.width > 0) {
          const tail = p.lineTail;
          const destTailX = Math.round(
            anchorX + (p.textXOffset || 0) + (renderedTextW > 0 ? (renderedTextW + tail.initialGap) : 0)
          );
          const destTailY = Math.round(tail.srcY + (p.textYOffset || 0));
          if (destTailX < W) {
            const drawTailW = Math.min(tail.width, W - destTailX);
            if (drawTailW > 0) {
              ctx.drawImage(
                offscreenCleanCanvas,
                tail.srcX,
                tail.srcY,
                drawTailW,
                tail.height,
                destTailX,
                destTailY,
                drawTailW,
                tail.height
              );
            }
          }
        }

        // 4.3 仅在鼠标悬停、拖拽移位、或刚框选出新一处待填字区域 (!p._consumed && !p.newText) 时显示极细提示框
        const isPendingNewBox = !p._consumed && !p.newText && idx === activePatchIndex;
        const shouldShowGuide = !options.forExport && currentTier === 'light' && (
          isPendingNewBox ||
          (isDraggingOnCanvas && dragMode === 'move_patch' && idx === activePatchIndex) ||
          idx === hoveredPatchIndex
        );
        if (shouldShowGuide) {
          ctx.save();
          ctx.strokeStyle = isPendingNewBox ? 'rgba(239, 68, 68, 0.9)' : 'rgba(59, 130, 246, 0.85)';
          ctx.lineWidth = isPendingNewBox ? 1.5 : 1;
          ctx.setLineDash([4, 3]);
          ctx.strokeRect(p.x - 2, p.y - 2, p.width + 4, p.height + 4);
          ctx.restore();
        }
      });
    }

    // 4B. 在「🎨 重绘级」模式下且尚未生成新图时，在原图上方绘制紫罗兰色待重绘区域框（原图内容 100% 保留可见，绝不擦除消失；导出时自动隐藏）
    if (!options.forExport && currentTier === 'heavy' && !heavyResultImgObj) {
      const heavyBoxes = getRegionTargetBoxes(currentBundle?.boxes || []);
      if (heavyBoxes.length > 0) {
        ctx.save();
        heavyBoxes.forEach((b, idx) => {
          ctx.fillStyle = 'rgba(124, 58, 237, 0.12)';
          ctx.fillRect(b.x, b.y, b.width, b.height);
          ctx.strokeStyle = '#7c3aed';
          ctx.lineWidth = 2;
          ctx.setLineDash([6, 4]);
          ctx.strokeRect(b.x, b.y, b.width, b.height);

          // 左上角绘制重绘区角标
          ctx.setLineDash([]);
          const tagText = `🎨 待重绘区域 #${idx + 1}`;
          ctx.font = 'bold 11px "Microsoft YaHei UI", sans-serif';
          const tw = ctx.measureText(tagText).width + 10;
          const th = 18;
          const tagY = b.y >= th + 2 ? b.y - th - 2 : b.y + 2;
          ctx.fillStyle = 'rgba(109, 40, 217, 0.92)';
          ctx.fillRect(b.x, tagY, tw, th);
          ctx.fillStyle = '#ffffff';
          ctx.textBaseline = 'middle';
          ctx.textAlign = 'left';
          ctx.fillText(tagText, b.x + 5, tagY + th / 2);
        });
        ctx.restore();
      }
    }

    // 5. 色温微调叠加 (暖色调/冷色调)
    if (filters.warmth !== 0) {
      ctx.save();
      const alpha = Math.min(0.28, Math.abs(filters.warmth) / 180);
      ctx.fillStyle = filters.warmth > 0
        ? `rgba(251, 146, 60, ${alpha})`
        : `rgba(56, 189, 248, ${alpha})`;
      ctx.globalCompositeOperation = 'soft-light';
      ctx.fillRect(0, 0, W, H);
      ctx.restore();
    }

    // 6. 画布上直接拖拽框选区域时的实时选区框预览（轻量级为红框，重绘级为紫罗兰框）
    if (!options.forExport && dragMode === 'draw_box' && tempManualBox) {
      ctx.save();
      const isHeavy = currentTier === 'heavy';
      ctx.strokeStyle = isHeavy ? '#7c3aed' : '#ef4444';
      ctx.lineWidth = 2;
      ctx.setLineDash([5, 3]);
      ctx.strokeRect(tempManualBox.x, tempManualBox.y, tempManualBox.width, tempManualBox.height);
      ctx.fillStyle = isHeavy ? 'rgba(124, 58, 237, 0.14)' : 'rgba(239, 68, 68, 0.10)';
      ctx.fillRect(tempManualBox.x, tempManualBox.y, tempManualBox.width, tempManualBox.height);
      ctx.restore();
    }
  }

  function exportResultDataUrl() {
    if (!cleanImgObj) return null;
    renderCanvas({ forExport: true });
    const dataUrl = aiEditCanvas.toDataURL('image/png');
    renderCanvas({ forExport: false });
    return dataUrl;
  }

  /**
   * 根据 currentBundle.boxes 实时重建黑白羽化蒙版 maskImgObj，确保在画布上直接框选重绘区后也能 100% 启用局部无损回贴
   */
  async function rebuildMaskFromBoxes() {
    if (!cleanImgObj || !currentBundle) return;
    const boxes = getRegionTargetBoxes(currentBundle.boxes || []);
    if (boxes.length === 0) {
      currentBundle.hasMaskRegion = false;
      currentBundle.maskDataUrl = null;
      maskImgObj = null;
      syncHeavyBoxStatusUI();
      return;
    }
    const W = cleanImgObj.naturalWidth || cleanImgObj.width;
    const H = cleanImgObj.naturalHeight || cleanImgObj.height;
    const mCanvas = document.createElement('canvas');
    mCanvas.width = W;
    mCanvas.height = H;
    const mCtx = mCanvas.getContext('2d');
    mCtx.fillStyle = '#000000';
    mCtx.fillRect(0, 0, W, H);
    mCtx.fillStyle = '#ffffff';
    boxes.forEach((b) => {
      mCtx.fillRect(b.x, b.y, b.width, b.height);
    });
    const mUrl = mCanvas.toDataURL('image/png');
    currentBundle.maskDataUrl = mUrl;
    currentBundle.hasMaskRegion = true;
    maskImgObj = await loadImagePromise(mUrl);
    syncHeavyBoxStatusUI();
  }

  function syncHeavyBoxStatusUI() {
    const boxes = getRegionTargetBoxes(currentBundle?.boxes || []);
    if (heavyBoxStatusText) {
      if (boxes.length > 0) {
        const last = boxes[boxes.length - 1];
        heavyBoxStatusText.textContent = `已框选 ${boxes.length} 处局部区域 (${Math.round(last.width)}×${Math.round(last.height)}px)`;
      } else {
        heavyBoxStatusText.textContent = '未框选 (在右图按住左键拖拽即可圈选局部)';
      }
    }
    if (btnClearHeavyBoxes) {
      btnClearHeavyBoxes.disabled = boxes.length === 0;
    }
  }

  function syncPatchTunerUI() {
    if (!lightLayerTuner) return;
    if (currentTier !== 'light') {
      lightLayerTuner.style.display = 'none';
      return;
    }
    lightLayerTuner.style.display = 'flex';
    selectActivePatch.innerHTML = '';

    if (patches.length === 0) {
      const emptyOpt = document.createElement('option');
      emptyOpt.value = '-1';
      emptyOpt.textContent = '未框选 (在右图拖拽框选)';
      selectActivePatch.appendChild(emptyOpt);
      if (btnDeletePatch) btnDeletePatch.disabled = true;
      if (tuneTextValue) {
        tuneTextValue.value = pendingTuneText || '';
        tuneTextValue.placeholder = '先在右图拖拽框选文字（或先输入新文字再去框选）';
      }
      return;
    }

    if (btnDeletePatch) btnDeletePatch.disabled = false;
    if (tuneTextValue) {
      tuneTextValue.placeholder = '直接输入替换后的新文字（实时生效，留空则仅擦除原字）';
    }

    if (activePatchIndex < 0 || activePatchIndex >= patches.length) {
      activePatchIndex = patches.length - 1;
    }

    patches.forEach((p, idx) => {
      const opt = document.createElement('option');
      opt.value = String(idx);
      const labelTxt = p.newText ? `图层 ${idx + 1}: "${p.newText.slice(0, 8)}"` : `图层 ${idx + 1}: [待填字/已擦除]`;
      opt.textContent = labelTxt;
      if (idx === activePatchIndex) opt.selected = true;
      selectActivePatch.appendChild(opt);
    });

    const cur = patches[activePatchIndex];
    if (!cur) return;
    tuneTextValue.value = cur.newText || '';
    if (tuneFillMode) {
      tuneFillMode.value = cur.fillMode || 'reflow';
    }
    tuneFontSize.value = String(Math.round(cur.fontSize || 18));
    tuneFontSizeLabel.textContent = String(Math.round(cur.fontSize || 18));
    tuneFontBold.checked = !!cur.isBold;
    tuneFontColor.value = cur.fontColor || '#1e293b';
    tuneBgColor.value = cur.bgColor || '#ffffff';
    tuneSmartErase.checked = cur.smartErase !== false;
  }

  function setTier(tier) {
    currentTier = tier === 'heavy' ? 'heavy' : 'light';
    if (tierCardLight) tierCardLight.classList.toggle('active', currentTier === 'light');
    if (tierCardHeavy) tierCardHeavy.classList.toggle('active', currentTier === 'heavy');

    if (btnAddManualPatch) {
      btnAddManualPatch.textContent = currentTier === 'heavy' ? '🎯 框选重绘区域' : '➕ 手动框选改字';
      btnAddManualPatch.title = currentTier === 'heavy'
        ? '在右侧图片上按住鼠标左键拖拽，圈出你想让 AI 重新设计的部位'
        : '无需消耗 Token，直接在右侧图上拖拽框选即可无痕擦字并打上新字';
    }

    if (currentTier === 'light') {
      if (lightFilterBox) lightFilterBox.style.display = 'flex';
      if (heavyOptionsBox) heavyOptionsBox.style.display = 'none';
      if (lightAiPromptToggleWrap) lightAiPromptToggleWrap.style.display = 'block';
      if (aiPromptGroup) aiPromptGroup.style.display = isLightAiPromptExpanded ? 'flex' : 'none';
      if (btnRunAiEdit) btnRunAiEdit.style.display = isLightAiPromptExpanded ? 'inline-flex' : 'none';
      if (btnRunAiEditText) btnRunAiEditText.textContent = '执行 AI 指令';
      if (aiPromptLabel) {
        aiPromptLabel.innerHTML = '🤖 AI 自然语言改字指令 <span style="font-weight: normal; color: var(--text-muted);">(免手动框选，让 AI 自动找字替换)</span>';
      }
      if (aiTokenCostTip) {
        aiTokenCostTip.textContent = isLightAiPromptExpanded
          ? '⚡ AI 识图模式：输入自然语言指令后点右侧按钮（耗约 200 Token）'
          : '⚡ 实时改字：在右图拖拽框选 ➡️ 在上方「替换文字」打字即刻生效（零 Token）';
        aiTokenCostTip.style.color = '#059669';
      }
    } else {
      if (lightFilterBox) lightFilterBox.style.display = 'none';
      if (heavyOptionsBox) heavyOptionsBox.style.display = 'flex';
      if (lightAiPromptToggleWrap) lightAiPromptToggleWrap.style.display = 'none';
      if (aiPromptGroup) aiPromptGroup.style.display = 'flex';
      if (btnRunAiEdit) btnRunAiEdit.style.display = 'inline-flex';
      if (btnRunAiEditText) btnRunAiEditText.textContent = '立即 AI 重绘';
      if (aiPromptLabel) {
        aiPromptLabel.innerHTML = '🎨 局部重新设计 / 重绘指令 <span style="font-weight: normal; color: var(--text-muted);">(在右图框选任意区域后描述新设计)</span>';
      }
      if (aiTokenCostTip) {
        aiTokenCostTip.textContent = '🎨 重绘级：在右图拖拽圈出局部区域 ➡️ 输入设计指令点右侧按钮';
        aiTokenCostTip.style.color = '#6d28d9';
      }
      syncHeavyBoxStatusUI();
    }

    syncPatchTunerUI();
    renderCanvas();
  }

  /**
   * 兜底检测：若用户未通过三图分离传入 boxes（例如直接从外部粘贴了一张已经画好红框的图片），
   * 严格按【空心矩形框几何拓扑】扫描鲜艳的红框 (R>195, G<70, B<70)：
   * 必须满足四条边覆盖率 >= 70% 且 88% 以上红像素集中在 3px 边框带内（彻底避免把画面原有的红色文字或红色小图标误判为红框！）
   */
  async function autoDetectAndCleanBakedRedBoxes() {
    if (!cleanImgObj || offscreenCleanCanvas.width === 0) return [];
    const W = offscreenCleanCanvas.width;
    const H = offscreenCleanCanvas.height;
    const imgData = offscreenCleanCtx.getImageData(0, 0, W, H);
    const data = imgData.data;

    const isRedPixel = (idx) => {
      const r = data[idx], g = data[idx + 1], b = data[idx + 2];
      return r > 195 && g < 70 && b < 70 && (r - g > 130) && (r - b > 130);
    };

    const visited = new Uint8Array(W * H);
    const detectedBoxes = [];

    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        const pIdx = y * W + x;
        if (visited[pIdx] || !isRedPixel(pIdx * 4)) continue;

        const queue = [pIdx];
        visited[pIdx] = 1;
        let minX = x, maxX = x, minY = y, maxY = y;
        const redCoords = [];

        while (queue.length > 0) {
          const cur = queue.pop();
          const cx = cur % W;
          const cy = (cur - cx) / W;
          redCoords.push({ x: cx, y: cy });
          if (cx < minX) minX = cx;
          if (cx > maxX) maxX = cx;
          if (cy < minY) minY = cy;
          if (cy > maxY) maxY = cy;

          const neighbors = [
            cy > 0 ? (cy - 1) * W + cx : -1,
            cy < H - 1 ? (cy + 1) * W + cx : -1,
            cx > 0 ? cy * W + (cx - 1) : -1,
            cx < W - 1 ? cy * W + (cx + 1) : -1
          ];
          for (const nb of neighbors) {
            if (nb >= 0 && !visited[nb] && isRedPixel(nb * 4)) {
              visited[nb] = 1;
              queue.push(nb);
            }
          }
        }

        const bw = maxX - minX + 1;
        const bh = maxY - minY + 1;
        if (bw < 20 || bh < 14) continue;

        // 严格验证是否为空心矩形框：统计落在外围 4px 边框带上的像素比例，以及上下左右四条边的覆盖度
        const borderBand = 4;
        let onPerimeterCount = 0;
        const topCols = new Uint8Array(bw);
        const botCols = new Uint8Array(bw);
        const leftRows = new Uint8Array(bh);
        const rightRows = new Uint8Array(bh);

        for (const rc of redCoords) {
          const onTop = rc.y <= minY + borderBand;
          const onBot = rc.y >= maxY - borderBand;
          const onLeft = rc.x <= minX + borderBand;
          const onRight = rc.x >= maxX - borderBand;
          if (onTop || onBot || onLeft || onRight) {
            onPerimeterCount++;
          }
          if (onTop) topCols[rc.x - minX] = 1;
          if (onBot) botCols[rc.x - minX] = 1;
          if (onLeft) leftRows[rc.y - minY] = 1;
          if (onRight) rightRows[rc.y - minY] = 1;
        }

        const perimeterRatio = onPerimeterCount / Math.max(1, redCoords.length);
        const sumArr = (arr) => {
          let s = 0;
          for (let i = 0; i < arr.length; i++) s += arr[i];
          return s;
        };
        const topCov = sumArr(topCols) / bw;
        const botCov = sumArr(botCols) / bw;
        const leftCov = sumArr(leftRows) / bh;
        const rightCov = sumArr(rightRows) / bh;

        if (perimeterRatio >= 0.85 && topCov >= 0.70 && botCov >= 0.70 && leftCov >= 0.70 && rightCov >= 0.70) {
          detectedBoxes.push({ type: 'Rect', x: minX, y: minY, width: bw, height: bh });
          // 将该红框及其抗锯齿红晕边缘 (外扩 1px) 从 offscreenCleanCanvas 上无痕抹除为外围背景色
          const outerX = Math.max(0, minX - 4);
          const outerY = Math.max(0, minY - 4);
          let bgR = 255, bgG = 255, bgB = 255;
          const sampleIdx = (outerY * W + outerX) * 4;
          if (!isRedPixel(sampleIdx)) {
            bgR = data[sampleIdx];
            bgG = data[sampleIdx + 1];
            bgB = data[sampleIdx + 2];
          }
          for (const rc of redCoords) {
            for (let dy = -1; dy <= 1; dy++) {
              for (let dx = -1; dx <= 1; dx++) {
                const nx = rc.x + dx;
                const ny = rc.y + dy;
                if (nx >= 0 && nx < W && ny >= 0 && ny < H) {
                  const nIdx = (ny * W + nx) * 4;
                  const r = data[nIdx], g = data[nIdx + 1], b = data[nIdx + 2];
                  if (r - g > 45 && r - b > 45) {
                    data[nIdx] = bgR;
                    data[nIdx + 1] = bgG;
                    data[nIdx + 2] = bgB;
                  }
                }
              }
            }
          }
        }
      }
    }

    if (detectedBoxes.length > 0) {
      offscreenCleanCtx.putImageData(imgData, 0, 0);
      const cleanedUrl = offscreenCleanCanvas.toDataURL('image/png');
      const cleanedImg = await loadImagePromise(cleanedUrl);
      if (cleanedImg) {
        cleanImgObj = cleanedImg;
        if (currentBundle) {
          currentBundle.cleanDataUrl = cleanedUrl;
        }
      }
    }
    return detectedBoxes;
  }

  async function loadBundle(bundle) {
    if (!bundle || !bundle.cleanDataUrl) return;
    currentBundle = bundle;
    heavyResultImgObj = null;
    patches = [];
    activePatchIndex = -1;
    hoveredPatchIndex = -1;
    lastConsumedPrompt = '';
    pendingTuneText = '';
    filters = { brightness: 100, contrast: 100, saturate: 100, warmth: 0 };
    resetFilterSlidersUI();

    cleanImgObj = await loadImagePromise(bundle.cleanDataUrl);
    annotatedImgObj = await loadImagePromise(bundle.annotatedDataUrl || bundle.cleanDataUrl);
    maskImgObj = bundle.maskDataUrl ? await loadImagePromise(bundle.maskDataUrl) : null;

    if (cleanImgObj) {
      const W = cleanImgObj.naturalWidth || cleanImgObj.width;
      const H = cleanImgObj.naturalHeight || cleanImgObj.height;
      offscreenCleanCanvas.width = W;
      offscreenCleanCanvas.height = H;
      offscreenCleanCtx.drawImage(cleanImgObj, 0, 0, W, H);
    }

    let nonTextBoxes = getRegionTargetBoxes(bundle.boxes || []);
    // 若传入的是单图（boxes 为空），自动检测图中是否有已画好的空心红框并剥离成干净原图 + 精确红框坐标！
    if (nonTextBoxes.length === 0 && (!bundle.boxes || bundle.boxes.length === 0)) {
      const autoBoxes = await autoDetectAndCleanBakedRedBoxes();
      if (autoBoxes.length > 0) {
        bundle.boxes = autoBoxes;
        bundle.hasMaskRegion = true;
        nonTextBoxes = autoBoxes;
      }
    }

    // 更新左上角缩略图与标注状态提示
    if (aiEditEmptyGuide) aiEditEmptyGuide.style.display = 'none';
    if (aiCanvasWrap) aiCanvasWrap.style.display = 'flex';
    if (aiEditThumbBar) {
      aiEditThumbBar.style.display = 'flex';
      aiEditAnnotatedThumb.src = bundle.annotatedDataUrl || bundle.cleanDataUrl;
    }

    const textNotes = bundle.textNotes || [];

    if (aiEditMaskBadge) {
      aiEditMaskBadge.textContent = nonTextBoxes.length > 0
        ? `已标 ${nonTextBoxes.length} 处修改区`
        : '直接框选改字';
    }

    if (aiEditBundleTitle && aiEditBundleSub) {
      if (nonTextBoxes.length > 0) {
        aiEditBundleTitle.textContent = `已分离干净原图与 ${nonTextBoxes.length} 处红框标注`;
        aiEditBundleSub.textContent = '直接在下方「替换文字」打字即可实时修改，在右图拉新框可改第 2 处';
      } else {
        aiEditBundleTitle.textContent = '已载入干净原图（直接框选改字）';
        aiEditBundleSub.textContent = '直接在右侧图片上拖拽框选要修改的文字，在左侧输入新文字实时生效';
      }
    }

    // 若用户在截屏时已经画了红框，进入工作台时立即将其转化为「文字图层 1、2...」，用户无需再点任何按钮，直接打字即实时替换！
    if (nonTextBoxes.length > 0) {
      nonTextBoxes.forEach((rb, idx) => {
        const noteText = textNotes[idx] !== undefined ? String(textNotes[idx]).trim() : (idx === 0 && textNotes[0] ? String(textNotes[0]).trim() : '');
        rb._consumed = !!noteText;
        const refined = analyzeAndRefineTextBox(rb.x + 1, rb.y + 1, Math.max(6, rb.width - 2), Math.max(6, rb.height - 2), {
          hasUserRedBox: true,
          newText: noteText
        });
        upsertPatch({
          id: 'patch_init_' + Date.now() + '_' + idx,
          x: refined.x,
          y: refined.y,
          width: refined.width,
          height: refined.height,
          glyphX: refined.glyphX,
          glyphY: refined.glyphY,
          glyphW: refined.glyphW,
          glyphH: refined.glyphH,
          hasRightNeighbor: refined.hasRightNeighbor,
          lineTail: refined.lineTail,
          fillMode: 'reflow',
          textXOffset: 0,
          textYOffset: 0,
          newText: noteText,
          fontSize: refined.fontSize,
          fontColor: refined.fontColor,
          bgColor: refined.bgColor,
          bgIsUniform: refined.bgIsUniform,
          isBold: refined.isBold,
          smartErase: true,
          align: 'left',
          _consumed: !!noteText
        });
      });
      activePatchIndex = 0;
    } else if (textNotes.length > 0) {
      pendingTuneText = textNotes.join(' ');
    }

    if (textNotes.length > 0 && aiEditPromptInput) {
      aiEditPromptInput.value = textNotes.join('；');
    }

    syncPatchTunerUI();
    renderCanvas();

    if (currentTier === 'light' && tuneTextValue) {
      setTimeout(() => {
        tuneTextValue.focus();
        tuneTextValue.select();
      }, 60);
    }
  }

  async function loadSingleImage(dataUrl) {
    await loadBundle({
      cleanDataUrl: dataUrl,
      annotatedDataUrl: dataUrl,
      maskDataUrl: null,
      hasMaskRegion: false,
      textNotes: [],
      boxes: []
    });
  }

  function resetFilterSlidersUI() {
    if (rngBrightness) rngBrightness.value = '100';
    if (rngContrast) rngContrast.value = '100';
    if (rngSaturate) rngSaturate.value = '100';
    if (rngWarmth) rngWarmth.value = '0';
    if (valBrightness) valBrightness.textContent = '100%';
    if (valContrast) valContrast.textContent = '100%';
    if (valSaturate) valSaturate.textContent = '100%';
    if (valWarmth) valWarmth.textContent = '0';
  }

  /**
   * 判断两个矩形区域是否指向同一处文字（重叠面积占较小框面积 > 25%）
   */
  function isRectOverlap(a, b) {
    if (!a || !b) return false;
    const xOverlap = Math.max(0, Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x));
    const yOverlap = Math.max(0, Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y));
    const interArea = xOverlap * yOverlap;
    const minArea = Math.max(1, Math.min(a.width * a.height, b.width * b.height));
    return interArea / minArea > 0.25;
  }

  /**
   * 增量添加或按坐标原位更新图层（绝不覆盖其他位置已经改好的文字图层！）
   */
  function upsertPatch(newPatch) {
    const existingIdx = patches.findIndex((p) => isRectOverlap(p, newPatch));
    if (existingIdx >= 0) {
      patches[existingIdx] = {
        ...patches[existingIdx],
        ...newPatch,
        id: patches[existingIdx].id
      };
      activePatchIndex = existingIdx;
      return existingIdx;
    }
    patches.push(newPatch);
    activePatchIndex = patches.length - 1;
    return activePatchIndex;
  }

  /**
   * 从用户指令中尝试提取针对红框/选区的直接替换文字（如“把红框里的文字改成：欣欣向荣”、“改成：XXX”）
   */
  function extractDirectTargetFromPrompt(promptStr) {
    const s = String(promptStr || '').trim();
    if (!s) return null;
    if (/^(?:请)?(?:帮我)?(?:把|将)?(?:红框|框里|框内|标注|选区).*?(?:水印|杂物|文字|内容)?(?:无痕)?(?:抹除|擦除|去掉|删除|清空)$/.test(s)) {
      return { isDirect: true, newText: '' };
    }
    const m = s.match(/^(?:请)?(?:帮我)?(?:把|将)?(?:图中|画面中)?(?:红框|框里|框内|圈选|标注)(?:里|内|中)?(?:的)?(?:文字|内容|字)?(?:修改|替换|改)(?:成|为)[:：\s]*["“「『]?(.+?)["”」』]?$/);
    if (m && m[1] !== undefined) {
      return { isDirect: true, newText: m[1].trim() };
    }
    const mShort = s.match(/^(?:修改|替换|改)(?:成|为)[:：\s]*["“「『]?(.+?)["”」』]?$/);
    if (mShort && mShort[1] !== undefined) {
      return { isDirect: true, newText: mShort[1].trim() };
    }
    return null;
  }

  /**
   * 当用户在工作台画布上直接手绘框选后，同步更新 currentBundle.boxes 与左上角缩略图状态
   */
  function syncCanvasBoxToBundle(bx, by, bw, bh, isConsumed = false, replaceInHeavy = false) {
    if (!currentBundle || !cleanImgObj) return null;
    if (!Array.isArray(currentBundle.boxes) || replaceInHeavy) {
      currentBundle.boxes = [];
    }
    const newBoxObj = { type: 'Rect', x: bx, y: by, width: bw, height: bh, _consumed: !!isConsumed };
    currentBundle.boxes.push(newBoxObj);
    currentBundle.hasMaskRegion = true;

    const W = cleanImgObj.naturalWidth || cleanImgObj.width;
    const H = cleanImgObj.naturalHeight || cleanImgObj.height;
    const annCanvas = document.createElement('canvas');
    annCanvas.width = W;
    annCanvas.height = H;
    const aCtx = annCanvas.getContext('2d');
    aCtx.drawImage(cleanImgObj, 0, 0, W, H);
    aCtx.strokeStyle = '#ef4444';
    aCtx.lineWidth = 2;
    getRegionTargetBoxes(currentBundle.boxes).forEach((b) => {
      aCtx.strokeRect(b.x, b.y, b.width, b.height);
    });
    const newAnnUrl = annCanvas.toDataURL('image/png');
    currentBundle.annotatedDataUrl = newAnnUrl;
    if (aiEditAnnotatedThumb) aiEditAnnotatedThumb.src = newAnnUrl;

    const count = getRegionTargetBoxes(currentBundle.boxes).length;
    if (aiEditMaskBadge) aiEditMaskBadge.textContent = `已标 ${count} 处修改区`;
    if (aiEditBundleTitle) aiEditBundleTitle.textContent = `已分离干净原图与 ${count} 处选区标注`;
    if (aiEditBundleSub) {
      aiEditBundleSub.textContent = currentTier === 'heavy'
        ? `已锁定重绘选区 (${bw}×${bh}px)，原图内容保留，输入设计指令后点击「立即 AI 重绘」`
        : '支持多处独立修改，之前改好的区域将自动保留不被覆盖';
    }
    rebuildMaskFromBoxes();
    return newBoxObj;
  }

  /**
   * 引擎一：⚡ 轻量级（支持多处独立增量修改、红框指令零延迟直通、像素级字形包络对齐与同行后文自动平移消留白）
   */
  async function runLightweightAiEdit() {
    const userPrompt = (aiEditPromptInput ? aiEditPromptInput.value.trim() : '') || '';
    const W = cleanImgObj.naturalWidth || cleanImgObj.width;
    const H = cleanImgObj.naturalHeight || cleanImgObj.height;
    const allRegionBoxes = getRegionTargetBoxes(currentBundle?.boxes || []);
    const unconsumedBoxes = allRegionBoxes.filter((b) => !b._consumed);
    const unconsumedPatches = patches.filter((p) => !p._consumed);
    const directTarget = extractDirectTargetFromPrompt(userPrompt);

    // 情况 A：用户指令明确写的是“把红框里的文字改成：XXX”，但图上没有任何红框或选区
    if (allRegionBoxes.length === 0 && patches.length === 0 && (directTarget || /红框|框里|框内|圈选/.test(userPrompt))) {
      isManualBoxMode = true;
      if (btnAddManualPatch) btnAddManualPatch.classList.add('btn-primary');
      if (aiEditCanvas) aiEditCanvas.style.cursor = 'crosshair';
      const hintTxt = directTarget && directTarget.newText ? `「${directTarget.newText}」` : '新文字';
      throw new Error(`请先在右侧图片上按住鼠标左键拖拽框选要改成${hintTxt}的原文字！（框选松手后将立即自动完成替换）`);
    }

    // 情况 B：用户指令是针对红框/选区的直接改字（如“把红框里的文字改成：XXX”）
    if (directTarget) {
      // B1. 若存在刚画好、尚未填字的待处理图层 (unconsumedPatches)，仅将新文字应用到这些新框上，100% 保留之前已改好的图层！
      if (unconsumedPatches.length > 0) {
        unconsumedPatches.forEach((p) => {
          const refined = analyzeAndRefineTextBox(p.x, p.y, p.width, p.height, {
            hasUserRedBox: true,
            newText: directTarget.newText
          });
          p.newText = directTarget.newText;
          p.fontSize = refined.fontSize;
          p._consumed = true;
        });
        allRegionBoxes.forEach((b) => { b._consumed = true; });
        lastConsumedPrompt = userPrompt;
        syncPatchTunerUI();
        renderCanvas();
        return;
      }

      // B2. 若存在尚未消费的截图红框 (unconsumedBoxes)，仅针对这些新红框生成修改图层并增量合并到 patches，绝不覆盖旧图层！
      if (unconsumedBoxes.length > 0) {
        unconsumedBoxes.forEach((rb, idx) => {
          rb._consumed = true;
          const refined = analyzeAndRefineTextBox(rb.x + 1, rb.y + 1, Math.max(6, rb.width - 2), Math.max(6, rb.height - 2), {
            hasUserRedBox: true,
            newText: directTarget.newText
          });
          upsertPatch({
            id: 'patch_' + Date.now() + '_' + idx,
            x: refined.x,
            y: refined.y,
            width: refined.width,
            height: refined.height,
            glyphX: refined.glyphX,
            glyphY: refined.glyphY,
            glyphW: refined.glyphW,
            glyphH: refined.glyphH,
            hasRightNeighbor: refined.hasRightNeighbor,
            lineTail: refined.lineTail,
            fillMode: 'reflow',
            textXOffset: 0,
            textYOffset: 0,
            newText: directTarget.newText,
            fontSize: refined.fontSize,
            fontColor: refined.fontColor,
            bgColor: refined.bgColor,
            bgIsUniform: refined.bgIsUniform,
            isBold: refined.isBold,
            smartErase: true,
            align: 'left',
            _consumed: true
          });
        });
        lastConsumedPrompt = userPrompt;
        syncPatchTunerUI();
        renderCanvas();
        return;
      }

      // B3. 若之前所有的红框都已经改过了 (unconsumedBoxes 为空且 unconsumedPatches 为空)，
      // 说明用户改完了第 1 处，现在在指令框输入了第 2 处的新文字并点了「立即 AI 改图」，但还没在图上框选第 2 处的位置！
      // 此时绝不能去覆盖第 1 处的文字！而是引导用户在右侧图上框选第 2 处（松手即自动替换）！
      isManualBoxMode = true;
      if (btnAddManualPatch) btnAddManualPatch.classList.add('btn-primary');
      if (aiEditCanvas) aiEditCanvas.style.cursor = 'crosshair';
      throw new Error(`💡 前 ${patches.length} 处修改已妥善保留！请直接在右侧图片上按住左键拖拽框选【下一处】要改成「${directTarget.newText}」的位置（松手即自动替换）`);
    }

    const cfg = configManager.get();
    if (!cfg.apiKey) {
      showToast('请先配置主 API Key');
      openSettings();
      return;
    }

    const effectivePrompt = userPrompt || '请识别标注区域的文字并进行微调';
    // 仅将尚未处理过的红框发给大模型，避免大模型重复修改已改过的红框
    const targetBoxesForAi = unconsumedBoxes.length > 0 ? unconsumedBoxes : [];

    // 保持原始分辨率比例，若宽高 <= 1440 则直接发送 1:1 原图尺寸，彻底杜绝压缩缩放造成的坐标偏移！
    const guideSourceUrl = (targetBoxesForAi.length > 0 && currentBundle?.annotatedDataUrl)
      ? currentBundle.annotatedDataUrl
      : currentBundle.cleanDataUrl;
    const maxDim = 1440;
    const scaleRatio = (W > maxDim || H > maxDim) ? Math.min(maxDim / W, maxDim / H) : 1;
    const sentW = Math.round(W * scaleRatio);
    const sentH = Math.round(H * scaleRatio);
    const compressedGuideUrl = scaleRatio < 1
      ? await compressImageIfNeeded(guideSourceUrl, sentW, sentH)
      : guideSourceUrl;

    const scaledBoxesForPrompt = targetBoxesForAi.map((b, i) => ({
      boxIndex: i,
      x: Math.round(b.x * scaleRatio),
      y: Math.round(b.y * scaleRatio),
      width: Math.round(b.width * scaleRatio),
      height: Math.round(b.height * scaleRatio)
    }));

    const boxesHint = scaledBoxesForPrompt.length > 0
      ? `用户在图片上用红框圈选了 ${scaledBoxesForPrompt.length} 处要修改的目标区域（图片宽 ${sentW}px，高 ${sentH}px）：${JSON.stringify(scaledBoxesForPrompt)}。请识别每个红框内部的原文字，并根据用户指令输出替换后的新文字。`
      : `图片宽 ${sentW}px，高 ${sentH}px。请根据用户指令精准定位要被替换的那几个字在图中的像素坐标 [x, y, width, height]。`;

    const sysPrompt = `你是一个高精度的图像文字微调与色彩精修解析引擎。
${boxesHint}
请根据用户的修改指令，输出严格的 JSON 格式（不要包含 markdown 代码块包裹，直接输出 JSON 对象）：
{
  "patches": [
    {
      "boxIndex": 0,
      "x": 整数像素横坐标,
      "y": 整数像素纵坐标,
      "width": 整数像素宽度,
      "height": 整数像素高度,
      "originalText": "仅填写被替换的那几个原文字（不要包含整句未改的文字）",
      "newText": "仅填写替换后的那几个新文字（若用户要求抹除/删除该处内容，则填空字符串 \"\"）",
      "align": "left"或"center"
    }
  ],
  "filters": {
    "brightness": 60到150之间的整数(默认100),
    "contrast": 60到160之间的整数(默认100),
    "saturate": 0到180之间的整数(默认100),
    "warmth": -40到40之间的整数(默认0)
  }
}`;

    const url = `${cfg.baseUrl.replace(/\/+$/, '')}/chat/completions`;
    const response = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${cfg.apiKey}`
      },
      body: JSON.stringify({
        model: cfg.visionModel || 'qwen-vl-max',
        temperature: 0.1,
        max_tokens: 600,
        messages: [
          { role: 'system', content: sysPrompt },
          {
            role: 'user',
            content: [
              { type: 'image_url', image_url: { url: compressedGuideUrl } },
              { type: 'text', text: `用户修改指令：${effectivePrompt}` }
            ]
          }
        ]
      })
    });

    if (!response.ok) {
      const errText = await response.text();
      throw new Error(`HTTP ${response.status}: ${errText.slice(0, 120)}`);
    }

    const data = await response.json();
    const rawContent = data.choices?.[0]?.message?.content || '{}';
    const jsonMatch = rawContent.match(/\{[\s\S]*\}/);
    if (!jsonMatch) {
      throw new Error('模型未能返回有效的改图结构数据，请重试或直接在右侧图片上拖拽框选文字');
    }

    const parsed = JSON.parse(jsonMatch[0]);

    // 1. 应用 AI 分析出的画面滤镜参数
    if (parsed.filters && typeof parsed.filters === 'object') {
      filters.brightness = Math.max(60, Math.min(150, Number(parsed.filters.brightness) || 100));
      filters.contrast = Math.max(60, Math.min(160, Number(parsed.filters.contrast) || 100));
      filters.saturate = Math.max(0, Math.min(180, Number(parsed.filters.saturate) || 100));
      filters.warmth = Math.max(-40, Math.min(40, Number(parsed.filters.warmth) || 0));
      if (rngBrightness) rngBrightness.value = String(filters.brightness);
      if (rngContrast) rngContrast.value = String(filters.contrast);
      if (rngSaturate) rngSaturate.value = String(filters.saturate);
      if (rngWarmth) rngWarmth.value = String(filters.warmth);
      if (valBrightness) valBrightness.textContent = `${filters.brightness}%`;
      if (valContrast) valContrast.textContent = `${filters.contrast}%`;
      if (valSaturate) valSaturate.textContent = `${filters.saturate}%`;
      if (valWarmth) valWarmth.textContent = String(filters.warmth);
    }

    // 2. 构建并通过像素级字形包络算法精准对齐每一个修改图层（使用 upsertPatch 增量合并，保留之前改过的所有其他位置！）
    const aiPatches = Array.isArray(parsed.patches) ? parsed.patches : [];

    if (aiPatches.length > 0) {
      aiPatches.forEach((ap, idx) => {
        const refBox = targetBoxesForAi[ap.boxIndex ?? idx] || (targetBoxesForAi.length === 1 ? targetBoxesForAi[0] : null);
        let initX, initY, initW, initH;
        let hasUserRedBox = false;

        if (refBox) {
          hasUserRedBox = true;
          refBox._consumed = true;
          initX = refBox.x + 1;
          initY = refBox.y + 1;
          initW = Math.max(6, refBox.width - 2);
          initH = Math.max(6, refBox.height - 2);
        } else {
          initX = Math.round((Number(ap.x) || 0) / scaleRatio);
          initY = Math.round((Number(ap.y) || 0) / scaleRatio);
          initW = Math.round((Number(ap.width) || 60) / scaleRatio);
          initH = Math.round((Number(ap.height) || 24) / scaleRatio);
        }

        const refined = analyzeAndRefineTextBox(initX, initY, initW, initH, {
          hasUserRedBox,
          originalText: ap.originalText || '',
          newText: ap.newText !== undefined ? String(ap.newText) : ''
        });

        upsertPatch({
          id: 'patch_' + Date.now() + '_' + idx,
          x: refined.x,
          y: refined.y,
          width: refined.width,
          height: refined.height,
          glyphX: refined.glyphX,
          glyphY: refined.glyphY,
          glyphW: refined.glyphW,
          glyphH: refined.glyphH,
          hasRightNeighbor: refined.hasRightNeighbor,
          lineTail: refined.lineTail,
          fillMode: ap.align === 'center' ? 'center' : 'reflow',
          textXOffset: 0,
          textYOffset: 0,
          newText: ap.newText !== undefined ? String(ap.newText) : '',
          fontSize: refined.fontSize,
          fontColor: refined.fontColor,
          bgColor: refined.bgColor,
          bgIsUniform: refined.bgIsUniform,
          isBold: refined.isBold,
          smartErase: true,
          align: ap.align === 'center' ? 'center' : 'left',
          _consumed: true
        });
      });
    } else if (targetBoxesForAi.length > 0 && !parsed.filters) {
      const fallbackNewText = effectivePrompt.replace(/^(把.*?(?:文字|内容)?改成[:：]?|改成[:：]?)/, '').trim();
      targetBoxesForAi.forEach((rb, idx) => {
        rb._consumed = true;
        const refined = analyzeAndRefineTextBox(rb.x + 1, rb.y + 1, Math.max(6, rb.width - 2), Math.max(6, rb.height - 2), {
          hasUserRedBox: true,
          newText: fallbackNewText
        });
        upsertPatch({
          id: 'patch_' + Date.now() + '_' + idx,
          x: refined.x,
          y: refined.y,
          width: refined.width,
          height: refined.height,
          glyphX: refined.glyphX,
          glyphY: refined.glyphY,
          glyphW: refined.glyphW,
          glyphH: refined.glyphH,
          hasRightNeighbor: refined.hasRightNeighbor,
          lineTail: refined.lineTail,
          fillMode: 'reflow',
          textXOffset: 0,
          textYOffset: 0,
          newText: fallbackNewText,
          fontSize: refined.fontSize,
          fontColor: refined.fontColor,
          bgColor: refined.bgColor,
          bgIsUniform: refined.bgIsUniform,
          isBold: refined.isBold,
          smartErase: true,
          align: 'left',
          _consumed: true
        });
      });
    }

    lastConsumedPrompt = userPrompt;
    syncPatchTunerUI();
    renderCanvas();
  }

  /**
   * 将远程图片 URL 转为本地 DataURL（防止跨域污染 Canvas 导致无法导出）
   */
  async function fetchImageAsDataUrl(imgUrl) {
    const res = await fetch(imgUrl);
    if (!res.ok) throw new Error(`下载生成的图片失败 (${res.status})`);
    const blob = await res.blob();
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onloadend = () => resolve(reader.result);
      reader.onerror = reject;
      reader.readAsDataURL(blob);
    });
  }

  /**
   * 常见重绘主体词精准视觉增强映射（防止“充电桩”因含“充电”二字被扩散模型误画成手机/充电宝等）
   */
  const SUBJECT_VISUAL_ENHANCE_MAP = [
    {
      pattern: /充电桩|电桩/,
      enhance: '新能源汽车立式充电桩（带充电枪线缆与科技显示屏的立柱式充电桩主体，3D futuristic EV charging pile station column with charging gun cable, single icon object centered, NO mobile phone, NO smartphone）'
    },
    {
      pattern: /服务器|机柜/,
      enhance: '科技感数据中心服务器机柜（3D futuristic server rack cabinet with glowing LED indicators, single icon centered）'
    },
    {
      pattern: /数据库/,
      enhance: '科技感多层圆柱体数据库图标（3D glowing cylindrical database icon, single object centered）'
    },
    {
      pattern: /基站|信号塔/,
      enhance: '通信基站信号发射塔（3D futuristic 5G communication base station tower icon, single object centered）'
    },
    {
      pattern: /路由器|网关|交换机/,
      enhance: '科技感无线路由器网络网关设备（3D futuristic network router gateway icon, single object centered）'
    },
    {
      pattern: /电池|储能/,
      enhance: '新能源储能电池模组图标（3D futuristic energy storage battery module icon, single object centered）'
    }
  ];

  /**
   * 提纯重绘指令：剥离“在右图框选的部分画”、“把红框区域重新设计成”等方位套话，
   * 同时 100% 保留用户在句中指定的宾语主体（如“把框选区域的【充电桩】重新设计成...”中的“充电桩”）！
   */
  function extractCleanSubjectPrompt(rawStr) {
    const orig = String(rawStr || '').trim();
    if (!orig) return { prompt: '', hasExplicitSubject: false, subjectNoun: '' };

    // 泛指占位词（不属于具体物体名称）
    const genericPlaceholders = /^(?:内容|物体|东西|元素|画面|图案|图形|图标|部分|区域|地方|位置|范围|背景|这里|这块|它|其|原图|图片|图像|样式|效果|风格)?$/;
    // 纯风格修饰描述（不包含具体要画的物体名词）
    const pureStylePattern = /^(?:精致的|高清的|立体的|酷炫的|现代的|扁平的|简约的|唯美的|逼真的)?\s*(?:3D|2\.5D|扁平化|赛博朋克|毛玻璃|霓虹|卡通|动漫|水彩|油画|像素|科技感|金属|发光|立体渲染|矢量|微缩|拟物)/i;

    // 匹配句式：“把框选区域的【充电桩】重新设计成【精致的3D立体渲染风格】” 或 “在右图框选的部分画【一个动漫女孩】”
    const prefixRe = /^(?:请)?(?:帮我)?(?:在|把|将|对)?(?:右图|右侧图片|右侧|图中|画面|图片|原图)?(?:上|里|内|中)?(?:的)?(?:红框|紫框|框选|圈选|选中|标注|这块|该)?(?:的)?(?:部分|区域|位置|地方|范围)?(?:里|内|中|上)?(?:面)?(?:的)?\s*([^，,。：:]{0,25}?)\s*(?:重新设计|设计|重绘|替换|修改|改|画|绘制|生成|变成|添加|加上|换成|转为|升级|渲染|制作)(?:成|为|出)?\s*(一个|一只|一张|一名|一座|一台|一部|一款)?[:：\s]*(.+)$/i;

    const match = orig.match(prefixRe);
    let subjectNoun = '';
    let mainBody = orig;
    let hasExplicitSubject = false;

    if (match) {
      const midCandidate = (match[1] || '').replace(/^(?:的|之|里|内|中|上)+|(?:\s|的)+$/g, '').trim();
      const quantifier = (match[2] || '').trim();
      const afterVerb = (match[3] || '').trim();

      if (midCandidate && !genericPlaceholders.test(midCandidate)) {
        // 用户在“框选区域的【XXX】重新设计成...”中明确写了具体主体名词（如“充电桩”）
        subjectNoun = midCandidate;
        hasExplicitSubject = true;
        mainBody = `${subjectNoun}，${quantifier}${afterVerb}`;
      } else {
        mainBody = `${quantifier}${afterVerb}`.trim();
        // 判断动词后面是否自带具体物体名词（如“一个动漫女孩” vs 纯风格词“精致的3D立体渲染风格”）
        if (mainBody && !pureStylePattern.test(mainBody)) {
          hasExplicitSubject = true;
          subjectNoun = mainBody;
        }
      }
    } else {
      // 仅剥离纯方位词前缀
      mainBody = orig
        .replace(/^(?:请)?(?:帮我)?(?:在|把|将)?(?:右图|右侧|图中)?(?:框选|圈选|红框|标注)(?:的)?(?:部分|区域)?(?:里|内|中)?[:：\s]*/i, '')
        .trim();
      if (mainBody && !pureStylePattern.test(mainBody)) {
        hasExplicitSubject = true;
      }
    }

    // 针对常见易混淆主体词（如“充电桩”）注入精准视觉特征与负向约束，彻底杜绝扩散模型把“充电桩”画成“手机”
    let finalPrompt = mainBody || orig;
    for (const item of SUBJECT_VISUAL_ENHANCE_MAP) {
      if (item.pattern.test(orig) || item.pattern.test(subjectNoun)) {
        finalPrompt = `${item.enhance}，${finalPrompt}`;
        hasExplicitSubject = true;
        break;
      }
    }

    return {
      prompt: finalPrompt,
      hasExplicitSubject,
      subjectNoun
    };
  }

  /**
   * 分析选区周边的原图背景色、明暗度与霓虹发光主色调，用于：
   * 1. 向生图 Prompt 注入与原图一致的背景色描述（如深黑科技底色 #0d1520 + 青色光效）；
   * 2. 为后续镶嵌合成提供原图底色参考与智能去底融合。
   */
  function analyzeBoxVisualEnvironment(box) {
    const defaultEnv = {
      bgR: 15, bgG: 23, bgB: 34,
      bgHex: '#0f1722',
      isDarkBg: true,
      hasCyanGlow: false,
      promptBgHint: '纯色干净背景，单主体居中完整呈现'
    };
    if (!offscreenCleanCtx || !cleanImgObj || !box) return defaultEnv;

    const W = cleanImgObj.naturalWidth || cleanImgObj.width;
    const H = cleanImgObj.naturalHeight || cleanImgObj.height;
    const bx = Math.max(0, Math.round(box.x));
    const by = Math.max(0, Math.round(box.y));
    const bw = Math.min(W - bx, Math.max(4, Math.round(box.width)));
    const bh = Math.min(H - by, Math.max(4, Math.round(box.height)));

    let imgData;
    try {
      imgData = offscreenCleanCtx.getImageData(bx, by, bw, bh).data;
    } catch (_) {
      return defaultEnv;
    }

    // 采样选区四周边框内侧 3px 的像素作为环境背景色样本，同时统计选区内是否存在青蓝色科技发光底座
    const borderSamples = [];
    let cyanGlowCount = 0;
    let totalPixels = bw * bh;

    for (let y = 0; y < bh; y++) {
      for (let x = 0; x < bw; x++) {
        const idx = (y * bw + x) * 4;
        const r = imgData[idx];
        const g = imgData[idx + 1];
        const b = imgData[idx + 2];
        const lum = 0.299 * r + 0.587 * g + 0.114 * b;

        // 检测青色/科技蓝发光像素 (G、B 显著高于 R)
        if (b > 130 && g > 110 && b - r > 50) {
          cyanGlowCount++;
        }

        // 采集四周边缘像素（排除底部 25% 处可能存在的发光底座高亮区）
        const isEdge = (x < 4 || x >= bw - 4 || y < 4);
        if (isEdge && y < bh * 0.78) {
          borderSamples.push({ r, g, b, lum });
        }
      }
    }

    if (borderSamples.length === 0) return defaultEnv;
    // 按亮度排序，取前 60% 较暗或中位区间计算稳健背景均值（排除边缘偶发的白色线条）
    borderSamples.sort((a, b) => a.lum - b.lum);
    const medianIdx = Math.floor(borderSamples.length * 0.35);
    const refSample = borderSamples[medianIdx] || borderSamples[0];
    const bgR = refSample.r;
    const bgG = refSample.g;
    const bgB = refSample.b;
    const bgLum = refSample.lum;
    const toHex = (n) => Math.max(0, Math.min(255, Math.round(n))).toString(16).padStart(2, '0');
    const bgHex = `#${toHex(bgR)}${toHex(bgG)}${toHex(bgB)}`;
    const isDarkBg = bgLum < 85;
    const isLightBg = bgLum > 195;
    const hasCyanGlow = cyanGlowCount > totalPixels * 0.03;

    let promptBgHint = '';
    if (isDarkBg) {
      promptBgHint = hasCyanGlow
        ? `背景必须为纯深黑科技色（${bgHex}），带有青蓝色科技微光氛围（cyan neon glow accent），单个立体图标主体居中悬浮，四周留有干净深黑边距，严禁出现白色或浅灰色背景块`
        : `背景必须为纯深色（${bgHex}），单个主体居中完整展示，四周留有干净深色边距，严禁出现白色或浅灰色背景块`;
    } else if (isLightBg) {
      promptBgHint = `背景必须为纯白色（${bgHex}）干净背景，单个主体居中完整展示，无多余杂物`;
    } else {
      promptBgHint = `背景必须为纯色（${bgHex}）干净背景，单个主体居中完整展示`;
    }

    return {
      bgR, bgG, bgB, bgHex, bgLum,
      isDarkBg, isLightBg, hasCyanGlow,
      promptBgHint
    };
  }

  /**
   * 当用户仅点击了预设风格标签（如“把框选区域重新设计成精致的3D立体渲染风格”）而未在文字中写明框内物体名称时，
   * 自动截取选区及其上下紧邻标签区域，通过已配置的视觉模型快速识别框内原本是什么物体（限时 3.8 秒，绝不阻塞生图）。
   */
  async function detectBoxSubjectWithVision(box) {
    const cfg = configManager.get();
    if (!cfg.apiKey || !cfg.baseUrl || !cleanImgObj || !box) return '';

    try {
      const W = cleanImgObj.naturalWidth || cleanImgObj.width;
      const H = cleanImgObj.naturalHeight || cleanImgObj.height;
      // 向上下左右适度外扩（尤其是上方 48px，以便把图标上方的“充电桩”等文字标签一起截入供视觉模型参考）
      const padX = Math.max(24, Math.round(box.width * 0.45));
      const padTop = Math.max(48, Math.round(box.height * 0.65));
      const padBottom = Math.max(24, Math.round(box.height * 0.35));

      const cx = Math.max(0, Math.round(box.x - padX));
      const cy = Math.max(0, Math.round(box.y - padTop));
      const cw = Math.min(W - cx, Math.round(box.width + padX * 2));
      const ch = Math.min(H - cy, Math.round(box.height + padTop + padBottom));

      const cropCanvas = document.createElement('canvas');
      cropCanvas.width = cw;
      cropCanvas.height = ch;
      const cCtx = cropCanvas.getContext('2d');
      cCtx.drawImage(cleanImgObj, cx, cy, cw, ch, 0, 0, cw, ch);
      // 用细红框标出核心目标区域
      cCtx.strokeStyle = '#ef4444';
      cCtx.lineWidth = 2;
      cCtx.strokeRect(box.x - cx, box.y - cy, box.width, box.height);
      const cropDataUrl = cropCanvas.toDataURL('image/png');

      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 3800);

      const url = `${cfg.baseUrl.replace(/\/+$/, '')}/chat/completions`;
      const res = await fetch(url, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${cfg.apiKey}`
        },
        signal: controller.signal,
        body: JSON.stringify({
          model: cfg.visionModel || 'qwen-vl-max',
          temperature: 0.1,
          max_tokens: 60,
          messages: [
            {
              role: 'user',
              content: [
                { type: 'image_url', image_url: { url: cropDataUrl } },
                {
                  type: 'text',
                  text: '请观察图中红框内的图标/物体（可结合红框紧邻的文字标签如“充电桩”等判断），直接输出红框内物体的具体名称与外观特征（例如：新能源汽车立式充电桩图标，青蓝发光科技风），不超过20个字，不要任何解释。'
                }
              ]
            }
          ]
        })
      });
      clearTimeout(timer);
      if (!res.ok) return '';
      const data = await res.json();
      const txt = (data.choices?.[0]?.message?.content || '').trim().replace(/[。！!？?\n]+/g, '');
      return txt.slice(0, 40);
    } catch (_) {
      return '';
    }
  }

  /**
   * 将生成的局部图像无缝合成到原图选区 (box) 内：
   * 1. 先清除原图选区中央的旧图标笔触（保留原图深色底纹与底部发光底座）；
   * 2. 自动检测生成图四角是否有纯色/浅灰背景块（当原图为深色背景而生成图带有浅灰/纯色底时，自动执行柔化抠底 Alpha 融合）；
   * 3. 对选区四周边缘施加柔化羽化过渡，彻底消除生硬的灰白方块补丁感！
   */
  function compositeGeneratedIntoBox(targetCtx, baseImg, genImg, box, envInfo) {
    const dx = Math.round(box.x);
    const dy = Math.round(box.y);
    const dw = Math.max(4, Math.round(box.width));
    const dh = Math.max(4, Math.round(box.height));

    // 1. 准备生成图的等比居中裁剪画布 (dw x dh)
    const genCanvas = document.createElement('canvas');
    genCanvas.width = dw;
    genCanvas.height = dh;
    const gCtx = genCanvas.getContext('2d');
    drawImageCover(gCtx, genImg, 0, 0, dw, dh);

    const genImgData = gCtx.getImageData(0, 0, dw, dh);
    const gData = genImgData.data;

    // 采样生成图四角与顶部边缘像素，计算生成图自身的背景底色 (genBgR, genBgG, genBgB)
    const cornerSamples = [];
    const sampleMarginX = Math.max(2, Math.min(8, Math.floor(dw * 0.12)));
    const sampleMarginY = Math.max(2, Math.min(8, Math.floor(dh * 0.12)));
    for (let y = 0; y < sampleMarginY; y++) {
      for (let x = 0; x < dw; x++) {
        if (x < sampleMarginX || x >= dw - sampleMarginX || y < 3) {
          const i = (y * dw + x) * 4;
          cornerSamples.push({ r: gData[i], g: gData[i + 1], b: gData[i + 2] });
        }
      }
    }

    let genBgR = 0, genBgG = 0, genBgB = 0;
    if (cornerSamples.length > 0) {
      cornerSamples.forEach((c) => {
        genBgR += c.r;
        genBgG += c.g;
        genBgB += c.b;
      });
      genBgR /= cornerSamples.length;
      genBgG /= cornerSamples.length;
      genBgB /= cornerSamples.length;
    }

    // 计算生成图四角背景的一致性（方差越小说明生成图四周是纯色/摄影棚背景）
    let cornerDiffSum = 0;
    cornerSamples.forEach((c) => {
      cornerDiffSum += Math.hypot(c.r - genBgR, c.g - genBgG, c.b - genBgB);
    });
    const avgCornerVariance = cornerSamples.length > 0 ? cornerDiffSum / cornerSamples.length : 99;
    const isUniformGenBg = avgCornerVariance < 32;

    // 判断生成图背景与原图选区背景是否存在明显色差（例如原图是深黑 #0d1520，而生成图是灰底/白底）
    const origBgR = envInfo?.bgR ?? 15;
    const origBgG = envInfo?.bgG ?? 23;
    const origBgB = envInfo?.bgB ?? 34;
    const bgMismatchDist = Math.hypot(genBgR - origBgR, genBgG - origBgG, genBgB - origBgB);
    const shouldKeyOutGenBg = isUniformGenBg && bgMismatchDist > 38;

    // 2. 若需要抠除生成图背景（或做边缘柔化融合），先抹除原图选区中央的旧 2D 白色图标线条，保留底色与底部 20% 的发光底座
    if (shouldKeyOutGenBg) {
      const basePatch = targetCtx.getImageData(dx, dy, dw, dh);
      const bData = basePatch.data;
      for (let y = 0; y < dh; y++) {
        // 保留底部 18% 的青色科技发光底座不被完全抹除，仅抹除上方 82% 区域内的高亮白色旧图标线条
        const vRatio = y / dh;
        const innerFade = vRatio < 0.78 ? 1.0 : Math.max(0, (0.92 - vRatio) / 0.14);
        if (innerFade <= 0) continue;

        for (let x = 0; x < dw; x++) {
          const i = (y * dw + x) * 4;
          const r = bData[i], g = bData[i + 1], b = bData[i + 2];
          const distFromOrigBg = Math.hypot(r - origBgR, g - origBgG, b - origBgB);
          // 若是高亮白色/浅色旧图标笔画，平滑替换为原图深色背景
          if (distFromOrigBg > 45) {
            bData[i] = Math.round(r * (1 - innerFade) + origBgR * innerFade);
            bData[i + 1] = Math.round(g * (1 - innerFade) + origBgG * innerFade);
            bData[i + 2] = Math.round(b * (1 - innerFade) + origBgB * innerFade);
          }
        }
      }
      targetCtx.putImageData(basePatch, dx, dy);
    }

    // 3. 对生成图执行智能背景透明化 (Chroma Key) + 四周边缘平滑羽化 (Feather)
    const featherX = Math.max(3, Math.round(dw * 0.1));
    const featherY = Math.max(3, Math.round(dh * 0.1));

    for (let y = 0; y < dh; y++) {
      const distTop = y;
      const distBottom = dh - 1 - y;
      const edgeFactorY = Math.min(1, Math.min(distTop, distBottom) / featherY);

      for (let x = 0; x < dw; x++) {
        const distLeft = x;
        const distRight = dw - 1 - x;
        const edgeFactorX = Math.min(1, Math.min(distLeft, distRight) / featherX);
        // 四周边缘柔化系数 (0 ~ 1)
        const edgeAlpha = Math.pow(edgeFactorX * edgeFactorY, 0.65);

        const i = (y * dw + x) * 4;
        const r = gData[i], g = gData[i + 1], b = gData[i + 2];

        let bgKeyAlpha = 1.0;
        if (shouldKeyOutGenBg) {
          const distToGenBg = Math.hypot(r - genBgR, g - genBgG, b - genBgB);
          // 距离生成图背景色小于 lowThresh 的像素完全透明（露出原图深色背景与底座），在 lowThresh~highThresh 之间平滑过渡
          const lowThresh = 22;
          const highThresh = 64;
          if (distToGenBg <= lowThresh) {
            bgKeyAlpha = 0.0;
          } else if (distToGenBg < highThresh) {
            bgKeyAlpha = (distToGenBg - lowThresh) / (highThresh - lowThresh);
          }
        }

        gData[i + 3] = Math.round(255 * Math.min(edgeAlpha, bgKeyAlpha));
      }
    }

    gCtx.putImageData(genImgData, 0, 0);
    targetCtx.drawImage(genCanvas, dx, dy, dw, dh);
  }

  /**
   * 等比居中裁剪绘制 (object-fit: cover)，防止生成图镶嵌进非正方形选区时发生拉伸变形
   */
  function drawImageCover(ctx, img, dx, dy, dw, dh) {
    const iw = img.naturalWidth || img.width || 1;
    const ih = img.naturalHeight || img.height || 1;
    const imgRatio = iw / ih;
    const boxRatio = dw / dh;
    let sx = 0, sy = 0, sw = iw, sh = ih;
    if (imgRatio > boxRatio) {
      sw = ih * boxRatio;
      sx = (iw - sw) / 2;
    } else {
      sh = iw / boxRatio;
      sy = (ih - sh) / 2;
    }
    ctx.drawImage(img, sx, sy, sw, sh, dx, dy, dw, dh);
  }

  /**
   * 引擎二：🎨 重绘级（支持魔搭社区免费 API 极速生图与重绘 / 硅基流动 DeepSeek Janus-Pro-7B / 阿里云百炼 / OpenAI 兼容生图接口 + 局部选区无损镶嵌）
   */
  async function runHeavyAiEdit() {
    const cfg = configManager.get();
    const provider = cfg.imageEditProvider || 'modelscope';
    const apiKey = (cfg.imageEditApiKey && cfg.imageEditApiKey.trim()) ? cfg.imageEditApiKey.trim() : (cfg.apiKey || '').trim();
    const defaultModelMap = {
      modelscope: 'Tongyi-MAI/Z-Image-Turbo',
      siliconflow: 'deepseek-ai/Janus-Pro-7B',
      dashscope: 'qwen-image-edit',
      openai: 'gpt-image-1'
    };
    const model = (cfg.imageEditModel && cfg.imageEditModel.trim())
      ? cfg.imageEditModel.trim()
      : (defaultModelMap[provider] || 'Tongyi-MAI/Z-Image-Turbo');

    if (!apiKey) {
      showToast('请先在「API 设置」中配置 API Key 或对应平台免费 Token');
      openSettings();
      return;
    }

    const rawPrompt = (aiEditPromptInput ? aiEditPromptInput.value.trim() : '');
    if (!rawPrompt) {
      showToast('请输入重绘修改指令（例如：把框选区域的充电桩重新设计成精致的3D立体渲染风格）');
      if (aiEditPromptInput) aiEditPromptInput.focus();
      return;
    }

    const W = cleanImgObj.naturalWidth || cleanImgObj.width;
    const H = cleanImgObj.naturalHeight || cleanImgObj.height;
    const nonTextBoxes = getRegionTargetBoxes(currentBundle?.boxes || []);
    const parsedPromptInfo = extractCleanSubjectPrompt(rawPrompt);
    let cleanSubjectPrompt = parsedPromptInfo.prompt;

    // 分析选区的原图环境背景色与光效特征
    const boxEnvInfo = nonTextBoxes.length > 0
      ? analyzeBoxVisualEnvironment(nonTextBoxes[0])
      : null;

    // 若用户框选了局部区域，但输入的指令中未包含具体物体名称（例如仅点击了“✨ 3D 立体化”快捷标签），
    // 自动调用视觉模型识别选区及紧邻标签中的原本物体名称，确保重绘主体 100% 对应原图内容！
    if (nonTextBoxes.length > 0 && !parsedPromptInfo.hasExplicitSubject) {
      if (aiEditLoadingText) {
        aiEditLoadingText.textContent = '🔍 正在识别框选区域的原始主体与光影环境...';
      }
      const detectedSubject = await detectBoxSubjectWithVision(nonTextBoxes[0]);
      if (detectedSubject) {
        // 检查识别出的物体是否命中专业增强词表（如“充电桩”）
        let enhancedDetected = detectedSubject;
        for (const item of SUBJECT_VISUAL_ENHANCE_MAP) {
          if (item.pattern.test(detectedSubject)) {
            enhancedDetected = `${item.enhance}`;
            break;
          }
        }
        cleanSubjectPrompt = `${enhancedDetected}，${cleanSubjectPrompt}`;
      }
    }

    // 将原图选区的背景色与光影约束拼接到生图 Prompt 中，使生成的图标底色与原图天然契合
    if (boxEnvInfo && boxEnvInfo.promptBgHint) {
      cleanSubjectPrompt = `${cleanSubjectPrompt}，${boxEnvInfo.promptBgHint}`;
    }

    // 根据选区宽高比自适应计算最佳生成分辨率（64 的整数倍，范围 512~1024），确保生成的图案比例与用户框选框完全吻合
    let genW = 1024;
    let genH = 1024;
    if (nonTextBoxes.length > 0) {
      const b = nonTextBoxes[0];
      const ratio = Math.max(0.5, Math.min(2.0, (b.width || 1) / (b.height || 1)));
      if (ratio >= 1) {
        genW = 768;
        genH = Math.max(512, Math.round((768 / ratio) / 64) * 64);
      } else {
        genH = 768;
        genW = Math.max(512, Math.round((768 * ratio) / 64) * 64);
      }
    }

    let fullPrompt = rawPrompt;
    if (nonTextBoxes.length > 0 && provider !== 'siliconflow') {
      const b = nonTextBoxes[0];
      const posDesc = `（目标修改区域位于画面横向约 ${Math.round((b.x / W) * 100)}%-${Math.round(((b.x + b.width) / W) * 100)}%，纵向约 ${Math.round((b.y / H) * 100)}%-${Math.round(((b.y + b.height) / H) * 100)}% 处，${cleanSubjectPrompt}，请保持其余区域不变）`;
      fullPrompt = `${rawPrompt} ${posDesc}`;
    }

    const cleanCompressed = await compressImageIfNeeded(currentBundle.cleanDataUrl, 1024, 1024);
    let generatedDataUrl = null;
    // 标记本次生成的图片是否为针对局部框选区生成的独立画面（需精准镶嵌进 nonTextBoxes 坐标内）
    let shouldEmbedIntoBoxes = nonTextBoxes.length > 0;

    if (provider === 'modelscope') {
      // 魔搭异步生图/改图通用提交与轮询执行器
      const executeModelScopeTask = async (reqBody, maxPollAttempts = 45, channelLabel = '魔搭免费算力') => {
        const submitRes = await fetch('https://api-inference.modelscope.cn/v1/images/generations', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${apiKey}`,
            'X-ModelScope-Async-Mode': 'true'
          },
          body: JSON.stringify(reqBody)
        });

        if (!submitRes.ok) {
          const errText = await submitRes.text();
          if (errText.includes('bind your Alibaba Cloud account')) {
            throw new Error('魔搭免费 API 提示：请先在魔搭官网 (modelscope.cn -> 账号设置) 绑定阿里云账号以激活免费算力');
          }
          if (errText.includes('email verification')) {
            throw new Error('魔搭免费 API 提示：请在魔搭「账号设置」中完成【邮箱绑定验证】即可领取免费算力');
          }
          throw new Error(`魔搭 API 响应异常 (${submitRes.status}): ${errText.slice(0, 120)}`);
        }

        const submitJson = await submitRes.json();
        if (submitJson.data && submitJson.data[0]) {
          const item = submitJson.data[0];
          return item.b64_json
            ? `data:image/png;base64,${item.b64_json}`
            : await fetchImageAsDataUrl(item.url);
        }
        if (!submitJson.task_id) {
          throw new Error('未识别的魔搭响应格式');
        }

        const taskId = submitJson.task_id;
        for (let attempt = 0; attempt < maxPollAttempts; attempt++) {
          await new Promise((r) => setTimeout(r, 2000));
          const elapsedSec = (attempt + 1) * 2;
          const pollRes = await fetch(`https://api-inference.modelscope.cn/v1/tasks/${taskId}`, {
            method: 'GET',
            headers: {
              'Authorization': `Bearer ${apiKey}`,
              'X-ModelScope-Task-Type': 'image_generation'
            }
          });
          if (!pollRes.ok) continue;
          const pollJson = await pollRes.json();
          const status = String(pollJson.task_status || pollJson.status || '').toUpperCase();
          const statusCn = status === 'RUNNING' || status === 'PROCESSING'
            ? 'GPU 渲染中'
            : (status === 'PENDING' || status === 'QUEUED' ? '排队分配 GPU 中' : '处理中');
          if (aiEditLoadingText) {
            aiEditLoadingText.textContent = `正在通过${channelLabel}重绘中 [${statusCn}] (${elapsedSec}s)...`;
          }

          if (status === 'SUCCEED' || status === 'SUCCESS' || status === 'COMPLETED') {
            const outUrl =
              pollJson.output_images?.[0] ||
              pollJson.output?.output_images?.[0] ||
              pollJson.data?.[0]?.url ||
              pollJson.result?.output_images?.[0] ||
              pollJson.images?.[0]?.url;
            if (!outUrl) throw new Error('重绘任务已完成但未返回图片地址');
            return await fetchImageAsDataUrl(outUrl);
          }
          if (status === 'FAILED' || status === 'CANCELED') {
            throw new Error(`重绘任务失败: ${pollJson.message || pollJson.error || status}`);
          }
        }
        return null; // 超时返回 null 以便上层自动切换极速备用通道
      };

      // 策略 1：当用户框选了局部区域或使用默认模型时，
      // 优先走阿里通义 8 步蒸馏极速模型 Tongyi-MAI/Z-Image-Turbo（常驻热启动，仅需 5~12 秒即可出图，绝不冷启动卡死！）
      const isDefaultOrTurbo = !model || model === 'Qwen/Qwen-Image-Edit' || /Z-Image-Turbo/i.test(model);
      if (nonTextBoxes.length > 0 && isDefaultOrTurbo) {
        shouldEmbedIntoBoxes = true;
        if (aiEditLoadingText) {
          aiEditLoadingText.textContent = '🚀 正在通过魔搭极速通道 (Z-Image-Turbo) 绘制选区画面...';
        }
        try {
          generatedDataUrl = await executeModelScopeTask({
            model: 'Tongyi-MAI/Z-Image-Turbo',
            prompt: cleanSubjectPrompt,
            width: genW,
            height: genH,
            num_inference_steps: 9,
            guidance_scale: 0.0
          }, 30, '魔搭极速通道');
        } catch (fastErr) {
          console.warn('Z-Image-Turbo 极速通道异常，自动切换 Qwen-Image 通道:', fastErr);
        }
      }

      // 策略 2：若未框选局部区域（全图编辑）或用户自定义了特定模型，或者极速通道未返回结果
      if (!generatedDataUrl) {
        const fallbackModel = (nonTextBoxes.length > 0 && isDefaultOrTurbo)
          ? 'Qwen/Qwen-Image'
          : (model === 'Qwen/Qwen-Image-Edit' ? 'Qwen/Qwen-Image-Edit-2509' : model);

        const isEditVariant = /Edit/i.test(fallbackModel);
        const reqPayload = {
          model: fallbackModel,
          prompt: nonTextBoxes.length > 0 ? cleanSubjectPrompt : fullPrompt
        };
        if (isEditVariant && nonTextBoxes.length === 0) {
          reqPayload.image_url = [cleanCompressed];
          reqPayload.images = [cleanCompressed];
          reqPayload.steps = 28;
          shouldEmbedIntoBoxes = false;
        } else {
          reqPayload.size = `${genW}x${genH}`;
          reqPayload.steps = 25;
        }

        generatedDataUrl = await executeModelScopeTask(reqPayload, 55, `魔搭 (${fallbackModel.split('/').pop()})`);

        // 终极兜底：若用户选了冷启动排队的重模型导致 110s 仍未出图，自动秒切 Z-Image-Turbo 救急，彻底告别超时报错！
        if (!generatedDataUrl && !/Z-Image-Turbo/i.test(fallbackModel)) {
          if (aiEditLoadingText) {
            aiEditLoadingText.textContent = '⚡ 原模型排队拥堵，已自动切换极速通道 (Z-Image-Turbo) 加速出图...';
          }
          shouldEmbedIntoBoxes = nonTextBoxes.length > 0;
          generatedDataUrl = await executeModelScopeTask({
            model: 'Tongyi-MAI/Z-Image-Turbo',
            prompt: cleanSubjectPrompt,
            width: genW,
            height: genH,
            num_inference_steps: 9,
            guidance_scale: 0.0
          }, 30, '魔搭极速通道');
        }
      }

      if (!generatedDataUrl) {
        throw new Error('魔搭免费算力当前排队繁忙超时，请稍后重试或在设置中切换至「🚀 硅基流动」');
      }
    } else if (provider === 'siliconflow') {
      // 渠道 B：硅基流动 API (api.siliconflow.cn) — 支持免费调用 DeepSeek 开源生图模型 deepseek-ai/Janus-Pro-7B 及 Kwai-Kolors/Kolors
      const sfBody = {
        model: model || 'deepseek-ai/Janus-Pro-7B',
        prompt: cleanSubjectPrompt
      };
      if (!/Janus/i.test(sfBody.model)) {
        sfBody.image_size = `${genW}x${genH}`;
        if (nonTextBoxes.length === 0) {
          sfBody.image = cleanCompressed;
        }
      }
      const sfRes = await fetch('https://api.siliconflow.cn/v1/images/generations', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${apiKey}`
        },
        body: JSON.stringify(sfBody)
      });

      if (!sfRes.ok) {
        const errText = await sfRes.text();
        throw new Error(`硅基流动生图接口报错 (${sfRes.status}): ${errText.slice(0, 120)}`);
      }

      const sfJson = await sfRes.json();
      const first = sfJson.images?.[0] || sfJson.data?.[0];
      if (!first) throw new Error('硅基流动接口未返回有效图片数据');
      generatedDataUrl = first.b64_json
        ? `data:image/png;base64,${first.b64_json}`
        : await fetchImageAsDataUrl(first.url);
    } else if (provider === 'dashscope') {
      // 渠道 C：阿里云百炼原生多模态图像编辑接口 (qwen-image-edit)
      shouldEmbedIntoBoxes = false;
      const dsRes = await fetch('https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${apiKey}`
        },
        body: JSON.stringify({
          model: model || 'qwen-image-edit',
          input: {
            messages: [
              {
                role: 'user',
                content: [
                  { image: cleanCompressed },
                  { text: fullPrompt }
                ]
              }
            ]
          },
          parameters: {
            n: 1
          }
        })
      });

      if (!dsRes.ok) {
        const errText = await dsRes.text();
        if (dsRes.status === 402 || dsRes.status === 429 || errText.includes('Arrearage') || errText.includes('Quota')) {
          throw new Error('当前阿里云百炼重绘模型额度不足！建议切换至「⚡轻量级」或在设置中改用「🌟魔搭社区 / 🚀硅基流动免费 API」');
        }
        throw new Error(`百炼重绘接口报错 (${dsRes.status}): ${errText.slice(0, 120)}`);
      }

      const dsJson = await dsRes.json();
      const imgUrl = dsJson.output?.choices?.[0]?.message?.content?.find((c) => c.image)?.image;
      if (!imgUrl) {
        throw new Error('百炼接口未返回编辑后的图片地址');
      }
      generatedDataUrl = await fetchImageAsDataUrl(imgUrl);
    } else {
      // 渠道 D：通用 OpenAI 兼容生图接口 (/v1/images/generations)
      const base = (cfg.baseUrl || 'https://api.openai.com/v1').replace(/\/+$/, '');
      if (base.includes('api.deepseek.com')) {
        throw new Error('DeepSeek 官方平台 (api.deepseek.com) 暂未开放图像生成接口！若需免费使用 DeepSeek 的 Janus-Pro-7B 生图模型，请在设置中将重绘渠道切换为「🚀 硅基流动」');
      }
      const oaiRes = await fetch(`${base}/images/generations`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${apiKey}`
        },
        body: JSON.stringify({
          model: model,
          prompt: nonTextBoxes.length > 0 ? cleanSubjectPrompt : fullPrompt,
          image: nonTextBoxes.length === 0 ? cleanCompressed : undefined
        })
      });

      if (!oaiRes.ok) {
        const errText = await oaiRes.text();
        throw new Error(`图像生成接口返回错误 (${oaiRes.status}): ${errText.slice(0, 120)}`);
      }

      const oaiJson = await oaiRes.json();
      const first = oaiJson.data?.[0] || oaiJson.images?.[0];
      if (!first) throw new Error('接口未返回有效图片数据');
      generatedDataUrl = first.b64_json
        ? `data:image/png;base64,${first.b64_json}`
        : await fetchImageAsDataUrl(first.url);
    }

    const rawGenImg = await loadImagePromise(generatedDataUrl);
    // 当用户在图上框选了局部重绘区域 (nonTextBoxes.length > 0) 时，
    // 使用 compositeGeneratedIntoBox 将生成的新图案经过智能去底、原底座保留与边缘羽化后精准合成到选区框内！
    if (rawGenImg && shouldEmbedIntoBoxes && nonTextBoxes.length > 0) {
      const compCanvas = document.createElement('canvas');
      compCanvas.width = W;
      compCanvas.height = H;
      const cCtx = compCanvas.getContext('2d');
      cCtx.drawImage(cleanImgObj, 0, 0, W, H);
      nonTextBoxes.forEach((b) => {
        const env = analyzeBoxVisualEnvironment(b);
        compositeGeneratedIntoBox(cCtx, cleanImgObj, rawGenImg, b, env);
      });
      heavyResultImgObj = await loadImagePromise(compCanvas.toDataURL('image/png'));
    } else {
      heavyResultImgObj = rawGenImg;
    }
    renderCanvas();
  }

  async function triggerAiEdit() {
    if (!cleanImgObj) {
      showToast('请先截图或粘贴一张图片');
      return;
    }
    if (isRunningAi) return;

    isRunningAi = true;
    if (aiEditLoadingOverlay) aiEditLoadingOverlay.style.display = 'flex';
    if (aiEditStatusBadge) aiEditStatusBadge.textContent = '生成中...';
    if (aiEditLoadingText) {
      aiEditLoadingText.textContent = currentTier === 'light'
        ? '⚡ 正在智能解析标注区域并原位无痕改字...'
        : '🎨 正在调用 AI 重绘模型重新设计画面...';
    }

    try {
      if (currentTier === 'light') {
        await runLightweightAiEdit();
        if (aiEditStatusBadge) aiEditStatusBadge.textContent = '已完成 (可拖拽微调)';
        showToast('✓ 轻量级改图完成！可直接拖拽画布文字或用左侧滑块微调');
      } else {
        await runHeavyAiEdit();
        if (aiEditStatusBadge) aiEditStatusBadge.textContent = '重绘完成';
        showToast('✓ AI 深度重绘完成！');
      }
    } catch (err) {
      if (aiEditStatusBadge) aiEditStatusBadge.textContent = '待框选/重试';
      showToast(err.message || 'AI 改图失败', 4200);
    } finally {
      isRunningAi = false;
      if (aiEditLoadingOverlay) aiEditLoadingOverlay.style.display = 'none';
    }
  }

  // 将鼠标事件坐标转换为画布内部真实图像像素坐标
  function getCanvasMousePt(e) {
    const rect = aiEditCanvas.getBoundingClientRect();
    const scaleX = aiEditCanvas.width / (rect.width || 1);
    const scaleY = aiEditCanvas.height / (rect.height || 1);
    return {
      x: (e.clientX - rect.left) * scaleX,
      y: (e.clientY - rect.top) * scaleY
    };
  }

  // 绑定所有 UI 与画布交互事件
  if (tierCardLight) tierCardLight.addEventListener('click', () => setTier('light'));
  if (tierCardHeavy) tierCardHeavy.addEventListener('click', () => setTier('heavy'));

  if (btnToggleLightAiPrompt) {
    btnToggleLightAiPrompt.addEventListener('click', () => {
      isLightAiPromptExpanded = !isLightAiPromptExpanded;
      btnToggleLightAiPrompt.textContent = isLightAiPromptExpanded
        ? '🔼 收起 AI 自然语言指令框（回到直接框选打字）'
        : '🤖 不想手动框选？点击展开 AI 自然语言自动找字修改 ▾';
      setTier('light');
      if (isLightAiPromptExpanded && aiEditPromptInput) {
        aiEditPromptInput.focus();
      }
    });
  }

  if (aiPromptQuickTags) {
    aiPromptQuickTags.querySelectorAll('.tag-btn').forEach((btn) => {
      btn.addEventListener('click', () => {
        const tpl = btn.dataset.prompt || '';
        if (aiEditPromptInput) {
          aiEditPromptInput.value = tpl;
          aiEditPromptInput.focus();
        }
        if (tpl.includes('重新设计')) {
          setTier('heavy');
        }
      });
    });
  }

  if (btnRunAiEdit) {
    btnRunAiEdit.addEventListener('click', triggerAiEdit);
  }

  if (aiEditPromptInput) {
    aiEditPromptInput.addEventListener('keydown', (e) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
        e.preventDefault();
        triggerAiEdit();
      }
    });
  }

  // 轻量级文字图层微调器绑定
  if (selectActivePatch) {
    selectActivePatch.addEventListener('change', () => {
      activePatchIndex = parseInt(selectActivePatch.value, 10) || 0;
      syncPatchTunerUI();
      renderCanvas();
    });
  }

  if (btnDeletePatch) {
    btnDeletePatch.addEventListener('click', () => {
      if (activePatchIndex >= 0 && activePatchIndex < patches.length) {
        patches.splice(activePatchIndex, 1);
        activePatchIndex = patches.length > 0 ? Math.min(activePatchIndex, patches.length - 1) : -1;
        syncPatchTunerUI();
        renderCanvas();
      }
    });
  }

  if (tuneTextValue) {
    tuneTextValue.addEventListener('input', () => {
      if (patches.length === 0 || activePatchIndex < 0) {
        pendingTuneText = tuneTextValue.value;
        isManualBoxMode = true;
        if (aiEditCanvas) aiEditCanvas.style.cursor = 'crosshair';
        if (aiEditStatusBadge) {
          aiEditStatusBadge.textContent = pendingTuneText
            ? '请在右图拖拽框选替换位置'
            : '就绪';
        }
        return;
      }
      const cur = patches[activePatchIndex];
      if (!cur) return;
      cur.newText = tuneTextValue.value;
      if (cur.newText) {
        cur._consumed = true;
      }
      if (selectActivePatch && selectActivePatch.options[activePatchIndex]) {
        selectActivePatch.options[activePatchIndex].textContent = cur.newText
          ? `图层 ${activePatchIndex + 1}: "${cur.newText.slice(0, 8)}"`
          : `图层 ${activePatchIndex + 1}: [待填字/已擦除]`;
      }
      if (aiEditStatusBadge) {
        aiEditStatusBadge.textContent = `已实时更新 (共 ${patches.length} 处)`;
      }
      renderCanvas();
    });
  }

  if (tuneFillMode) {
    tuneFillMode.addEventListener('change', () => {
      const cur = patches[activePatchIndex];
      if (!cur) return;
      cur.fillMode = tuneFillMode.value || 'reflow';
      cur.align = cur.fillMode === 'center' ? 'center' : 'left';
      renderCanvas();
    });
  }

  if (tuneFontSize) {
    tuneFontSize.addEventListener('input', () => {
      const cur = patches[activePatchIndex];
      if (!cur) return;
      cur.fontSize = Number(tuneFontSize.value) || 18;
      if (tuneFontSizeLabel) tuneFontSizeLabel.textContent = String(cur.fontSize);
      renderCanvas();
    });
  }

  if (tuneFontBold) {
    tuneFontBold.addEventListener('change', () => {
      const cur = patches[activePatchIndex];
      if (!cur) return;
      cur.isBold = tuneFontBold.checked;
      renderCanvas();
    });
  }

  if (tuneFontColor) {
    tuneFontColor.addEventListener('input', () => {
      const cur = patches[activePatchIndex];
      if (!cur) return;
      cur.fontColor = tuneFontColor.value;
      renderCanvas();
    });
  }

  if (tuneBgColor) {
    tuneBgColor.addEventListener('input', () => {
      const cur = patches[activePatchIndex];
      if (!cur) return;
      cur.bgColor = tuneBgColor.value;
      // 手动选底色时自动切换为纯色填充以便立即看到底色效果
      cur.smartErase = false;
      if (tuneSmartErase) tuneSmartErase.checked = false;
      renderCanvas();
    });
  }

  if (tuneSmartErase) {
    tuneSmartErase.addEventListener('change', () => {
      const cur = patches[activePatchIndex];
      if (!cur) return;
      cur.smartErase = tuneSmartErase.checked;
      renderCanvas();
    });
  }

  // 滤镜滑块绑定
  function onFilterSliderInput() {
    filters.brightness = Number(rngBrightness?.value) || 100;
    filters.contrast = Number(rngContrast?.value) || 100;
    filters.saturate = Number(rngSaturate?.value) || 100;
    filters.warmth = Number(rngWarmth?.value) || 0;
    if (valBrightness) valBrightness.textContent = `${filters.brightness}%`;
    if (valContrast) valContrast.textContent = `${filters.contrast}%`;
    if (valSaturate) valSaturate.textContent = `${filters.saturate}%`;
    if (valWarmth) valWarmth.textContent = String(filters.warmth);
    renderCanvas();
  }

  [rngBrightness, rngContrast, rngSaturate, rngWarmth].forEach((el) => {
    if (el) el.addEventListener('input', onFilterSliderInput);
  });

  if (btnResetFilters) {
    btnResetFilters.addEventListener('click', () => {
      filters = { brightness: 100, contrast: 100, saturate: 100, warmth: 0 };
      resetFilterSlidersUI();
      renderCanvas();
    });
  }

  if (chkLockNonMask) {
    chkLockNonMask.addEventListener('change', () => renderCanvas());
  }

  if (btnClearHeavyBoxes) {
    btnClearHeavyBoxes.addEventListener('click', () => {
      if (currentBundle) {
        currentBundle.boxes = [];
        currentBundle.hasMaskRegion = false;
        currentBundle.maskDataUrl = null;
      }
      maskImgObj = null;
      heavyResultImgObj = null;
      syncHeavyBoxStatusUI();
      if (aiEditMaskBadge) aiEditMaskBadge.textContent = '全图重绘 / 待框选';
      renderCanvas();
      showToast('已清除重绘选区框，可在右图重新拖拽框选局部区域');
    });
  }

  // 手动框选按钮（自适应当前所处的轻量级或重绘级模式，绝不强行跳回轻量级！）
  if (btnAddManualPatch) {
    btnAddManualPatch.addEventListener('click', () => {
      if (!cleanImgObj) {
        showToast('请先截图或载入图片');
        return;
      }
      isManualBoxMode = !isManualBoxMode;
      btnAddManualPatch.classList.toggle('btn-primary', isManualBoxMode);
      if (aiEditCanvas) {
        aiEditCanvas.style.cursor = isManualBoxMode ? 'crosshair' : 'default';
      }
      if (isManualBoxMode) {
        showToast(currentTier === 'heavy'
          ? '🎨 请在右侧图片上按住鼠标左键拖拽，框选要重新设计的局部区域'
          : '✏️ 请在右侧图片上按住鼠标左键拖拽，框选要修改的文字区域');
      }
    });
  }

  // 画布鼠标交互：支持直接在图片上按住左键拖拽画选区框 & 拖拽已有文字微调位置
  if (aiEditCanvas) {
    aiEditCanvas.addEventListener('mousedown', (e) => {
      if (!cleanImgObj || e.button !== 0) return;
      const pt = getCanvasMousePt(e);

      if (isManualBoxMode) {
        isDraggingOnCanvas = true;
        dragMode = 'draw_box';
        dragStartPt = pt;
        tempManualBox = { x: pt.x, y: pt.y, width: 0, height: 0 };
        return;
      }

      // 命中检测：是否点中了某个轻量级修改图层（可拖拽微调文字位置）
      if (currentTier === 'light' && patches.length > 0) {
        for (let i = patches.length - 1; i >= 0; i--) {
          const p = patches[i];
          if (pt.x >= p.x - 6 && pt.x <= p.x + p.width + 6 && pt.y >= p.y - 6 && pt.y <= p.y + p.height + 6) {
            activePatchIndex = i;
            isDraggingOnCanvas = true;
            dragMode = 'move_patch';
            dragStartPt = pt;
            dragInitialOffset = { x: p.textXOffset || 0, y: p.textYOffset || 0 };
            aiEditCanvas.style.cursor = 'move';
            syncPatchTunerUI();
            renderCanvas();
            return;
          }
        }
      }

      // 若未点中已有图层，允许用户直接在画布任意位置按住左键拖拽画选区框！
      isDraggingOnCanvas = true;
      dragMode = 'draw_box';
      dragStartPt = pt;
      tempManualBox = { x: pt.x, y: pt.y, width: 0, height: 0 };
    });

    aiEditCanvas.addEventListener('mousemove', (e) => {
      if (isDraggingOnCanvas || isManualBoxMode || !cleanImgObj) return;
      const pt = getCanvasMousePt(e);
      let hitIdx = -1;
      if (currentTier === 'light' && patches.length > 0) {
        for (let i = patches.length - 1; i >= 0; i--) {
          const p = patches[i];
          if (pt.x >= p.x - 6 && pt.x <= p.x + p.width + 6 && pt.y >= p.y - 6 && pt.y <= p.y + p.height + 6) {
            hitIdx = i;
            break;
          }
        }
      }
      if (hitIdx !== hoveredPatchIndex) {
        hoveredPatchIndex = hitIdx;
        aiEditCanvas.style.cursor = hitIdx >= 0 ? 'move' : 'crosshair';
        renderCanvas();
      }
    });

    aiEditCanvas.addEventListener('mouseleave', () => {
      if (hoveredPatchIndex !== -1 && !isDraggingOnCanvas) {
        hoveredPatchIndex = -1;
        aiEditCanvas.style.cursor = 'crosshair';
        renderCanvas();
      }
    });

    window.addEventListener('mousemove', (e) => {
      if (!isDraggingOnCanvas || !cleanImgObj) return;
      const pt = getCanvasMousePt(e);

      if (dragMode === 'draw_box') {
        const x = Math.min(dragStartPt.x, pt.x);
        const y = Math.min(dragStartPt.y, pt.y);
        const width = Math.abs(pt.x - dragStartPt.x);
        const height = Math.abs(pt.y - dragStartPt.y);
        tempManualBox = { x, y, width, height };
        renderCanvas();
      } else if (dragMode === 'move_patch') {
        const cur = patches[activePatchIndex];
        if (!cur) return;
        cur.textXOffset = Math.round(dragInitialOffset.x + (pt.x - dragStartPt.x));
        cur.textYOffset = Math.round(dragInitialOffset.y + (pt.y - dragStartPt.y));
        renderCanvas();
      }
    });

    window.addEventListener('mouseup', () => {
      if (!isDraggingOnCanvas) return;
      isDraggingOnCanvas = false;

      if (dragMode === 'draw_box') {
        isManualBoxMode = false;
        if (btnAddManualPatch) btnAddManualPatch.classList.remove('btn-primary');
        if (aiEditCanvas) aiEditCanvas.style.cursor = 'crosshair';

        if (tempManualBox && tempManualBox.width >= 8 && tempManualBox.height >= 8) {
          const bx = Math.round(tempManualBox.x);
          const by = Math.round(tempManualBox.y);
          const bw = Math.round(tempManualBox.width);
          const bh = Math.round(tempManualBox.height);

          // 若当前处于「🎨 重绘级」模式，框选仅用于标记局部重绘区域（保留原图不擦除，绘制紫罗兰选区框并生成羽化蒙版）
          if (currentTier === 'heavy') {
            heavyResultImgObj = null;
            syncCanvasBoxToBundle(bx, by, bw, bh, false, true);
            tempManualBox = null;
            if (aiEditStatusBadge) {
              aiEditStatusBadge.textContent = `已圈选重绘区 (${bw}×${bh})`;
            }
            renderCanvas();
            if (aiEditPromptInput) aiEditPromptInput.focus();
            showToast('✓ 已锁定局部重绘区域！请在左侧描述想如何重新设计，再点「立即 AI 重绘」');
            dragMode = 'none';
            return;
          }

          // 轻量级模式：优先使用用户在未框选时预填的 pendingTuneText；否则创建一个干净的图层并立刻聚焦到「替换文字」输入框！
          let autoNewText = '';
          let isNowConsumed = false;

          if (pendingTuneText) {
            autoNewText = pendingTuneText;
            pendingTuneText = '';
            isNowConsumed = true;
          } else if (isLightAiPromptExpanded && aiEditPromptInput) {
            const curPrompt = aiEditPromptInput.value.trim();
            const isPromptFresh = curPrompt.length > 0 && curPrompt !== lastConsumedPrompt;
            const promptTarget = isPromptFresh ? extractDirectTargetFromPrompt(curPrompt) : null;
            if (promptTarget) {
              autoNewText = promptTarget.newText;
              isNowConsumed = true;
              lastConsumedPrompt = curPrompt;
            }
          }

          syncCanvasBoxToBundle(bx, by, bw, bh, isNowConsumed);

          const refined = analyzeAndRefineTextBox(bx, by, bw, bh, {
            hasUserRedBox: true,
            newText: autoNewText
          });
          upsertPatch({
            id: 'patch_' + Date.now(),
            x: refined.x,
            y: refined.y,
            width: refined.width,
            height: refined.height,
            glyphX: refined.glyphX,
            glyphY: refined.glyphY,
            glyphW: refined.glyphW,
            glyphH: refined.glyphH,
            hasRightNeighbor: refined.hasRightNeighbor,
            lineTail: refined.lineTail,
            fillMode: 'reflow',
            textXOffset: 0,
            textYOffset: 0,
            newText: autoNewText,
            fontSize: refined.fontSize,
            fontColor: refined.fontColor,
            bgColor: refined.bgColor,
            bgIsUniform: refined.bgIsUniform,
            isBold: refined.isBold,
            smartErase: true,
            align: 'left',
            _consumed: isNowConsumed
          });
          tempManualBox = null;
          syncPatchTunerUI();
          renderCanvas();
          if (aiEditStatusBadge) {
            aiEditStatusBadge.textContent = autoNewText
              ? `已完成 ${patches.length} 处 (可拖拽微调)`
              : `已选第 ${patches.length} 处 (打字即实时替换)`;
          }
          if (tuneTextValue) {
            tuneTextValue.focus();
            tuneTextValue.select();
          }
          showToast(autoNewText
            ? `✓ 第 ${patches.length} 处已替换为「${autoNewText}」！`
            : `✓ 已框选第 ${patches.length} 处！请直接打字输入替换文字（实时生效）`);
        } else {
          tempManualBox = null;
          renderCanvas();
        }
      } else if (dragMode === 'move_patch') {
        if (aiEditCanvas) aiEditCanvas.style.cursor = hoveredPatchIndex >= 0 ? 'move' : 'crosshair';
        renderCanvas();
      }
      dragMode = 'none';
    });
  }

  // 点击左上角标注缩略图放大预览
  if (aiEditAnnotatedThumb) {
    aiEditAnnotatedThumb.addEventListener('click', () => {
      if (aiEditAnnotatedThumb.src && imageLightbox && lightboxImg) {
        lightboxImg.src = aiEditAnnotatedThumb.src;
        imageLightbox.classList.add('active');
      }
    });
  }

  // 按住对比原图
  if (btnHoldCompare) {
    const startCompare = () => {
      if (!cleanImgObj) return;
      isComparingOriginal = true;
      renderCanvas();
    };
    const endCompare = () => {
      if (!isComparingOriginal) return;
      isComparingOriginal = false;
      renderCanvas();
    };
    btnHoldCompare.addEventListener('mousedown', startCompare);
    btnHoldCompare.addEventListener('mouseup', endCompare);
    btnHoldCompare.addEventListener('mouseleave', endCompare);
  }

  // 以当前结果为基础继续下一轮修改
  if (btnUseAsBase) {
    btnUseAsBase.addEventListener('click', async () => {
      const mergedUrl = exportResultDataUrl();
      if (!mergedUrl) return;
      latestCaptureBundle = null;
      rawImageBase64 = mergedUrl;
      currentImageBase64 = mergedUrl;
      await loadSingleImage(mergedUrl);
      showToast('✓ 已将当前改图结果固化为新底图，可继续下一轮修改');
    });
  }

  // 四大交付动作：转 OCR / 贴到桌面 / 保存图片 / 复制新图
  if (btnAiSendOcr) {
    btnAiSendOcr.addEventListener('click', () => {
      const mergedUrl = exportResultDataUrl();
      if (!mergedUrl) return;
      latestCaptureBundle = null;
      switchWorkspaceMode('ocr');
      handleNewImage(mergedUrl);
    });
  }

  if (btnAiPin) {
    btnAiPin.addEventListener('click', () => {
      const mergedUrl = exportResultDataUrl();
      if (!mergedUrl) return;
      if (window.nativeBridge && typeof window.nativeBridge.pinImageDataUrl === 'function') {
        const ok = window.nativeBridge.pinImageDataUrl(mergedUrl);
        if (ok) {
          showToast('📌 已将修改后的新图钉在桌面上 (双击或按 Esc 关闭贴图)');
          return;
        }
      }
      showToast('当前环境不支持桌面贴图');
    });
  }

  if (btnAiSave) {
    btnAiSave.addEventListener('click', () => {
      const mergedUrl = exportResultDataUrl();
      if (!mergedUrl) return;
      const a = document.createElement('a');
      a.href = mergedUrl;
      a.download = `AI_Edited_${Date.now()}.png`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      showToast('💾 已触发保存高清 PNG 图片');
    });
  }

  if (btnAiCopy) {
    btnAiCopy.addEventListener('click', () => {
      const mergedUrl = exportResultDataUrl();
      if (!mergedUrl) return;
      if (window.nativeBridge && typeof window.nativeBridge.copyImageDataUrl === 'function') {
        const ok = window.nativeBridge.copyImageDataUrl(mergedUrl);
        if (ok) {
          showToast('✓ 修改后的新图片已复制到系统剪贴板！');
          return;
        }
      }
      showToast('复制图片失败，请尝试点击「保存」');
    });
  }

  return {
    loadBundle,
    loadSingleImage,
    hasImage: () => !!cleanImgObj
  };
})();

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
  if (activeWorkspaceMode === 'ai_edit' && aiEditController.hasImage()) return;

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

    // 判断是否是由 "ocr截图" / "截图" / "截屏" / "截图改图" 等独立关键词唤起直接截图
    const isDirectCapture = action && (
      action.code === 'direct-capture' ||
      (typeof action.payload === 'string' && (
        action.payload === 'ocr截图' ||
        action.payload === '截图' ||
        action.payload === '截屏' ||
        action.payload === '屏幕截图' ||
        action.payload === '截图改图'
      ))
    );

    if (isDirectCapture) {
      hasAutoTriggeredCapture = true;
      let intent = 'auto';
      if (action.payload === '截图改图') {
        intent = 'ai_edit';
      } else if (action.payload === 'ocr截图') {
        intent = 'ocr';
      }
      if (window.nativeBridge && window.nativeBridge.log) {
        window.nativeBridge.log(`>>> 匹配到直接截图指令(direct-capture, intent=${intent})，立即唤起截图 <<<`);
      }
      setTimeout(() => {
        captureManager.startCapture(intent);
      }, 60);
      return;
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
