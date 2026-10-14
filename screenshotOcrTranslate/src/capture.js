/**
 * capture.js - 纯 JS 原生截图标注与悬浮工具栏引擎 (DPI 比例感知 + 智能停靠 + 零跳屏)
 * 1:1 对齐 Snipaste / 原版 C# Snipper 18 项工具与二级属性栏
 */

(function () {
  'use strict';

  // 1. DOM 元素获取
  const stageContainer = document.getElementById('stageContainer');
  const bgCanvas = document.getElementById('bgCanvas');
  const annotCanvas = document.getElementById('annotCanvas');
  const sizeBadge = document.getElementById('sizeBadge');
  const inlineTextInput = document.getElementById('inlineTextInput');

  const bgCtx = bgCanvas.getContext('2d');
  const annotCtx = annotCanvas.getContext('2d');

  // 工具栏元素
  const toolbarWrap = document.getElementById('toolbarWrap');
  const floatingToolbar = document.getElementById('floatingToolbar');
  const subPalette = document.getElementById('subPalette');
  const subParamBadge = document.getElementById('subParamBadge');
  const paletteSep = document.getElementById('paletteSep');

  // 二级子配置区
  const subShapeOptions = document.getElementById('subShapeOptions');
  const subLineOptions = document.getElementById('subLineOptions');
  const subMosaicOptions = document.getElementById('subMosaicOptions');
  const subNumberOptions = document.getElementById('subNumberOptions');
  const subTextOptions = document.getElementById('subTextOptions');

  // 二级子控制控件
  const btnToggleFill = document.getElementById('btnToggleFill');
  const fillIconInner = document.getElementById('fillIconInner');
  const btnToggleDash = document.getElementById('btnToggleDash');
  const btnArrowStyle = document.getElementById('btnArrowStyle');
  const arrowStyleLabel = document.getElementById('arrowStyleLabel');
  const btnToggleBlurMode = document.getElementById('btnToggleBlurMode');
  const blurModeLabel = document.getElementById('blurModeLabel');
  const rngMosaicSize = document.getElementById('rngMosaicSize');
  const valMosaicSize = document.getElementById('valMosaicSize');
  const btnResetNumber = document.getElementById('btnResetNumber');
  const btnTextBold = document.getElementById('btnTextBold');
  const btnTextItalic = document.getElementById('btnTextItalic');
  const btnTextBg = document.getElementById('btnTextBg');
  const selFontSize = document.getElementById('selFontSize');

  // 调色板
  const currentSwatch = document.getElementById('currentSwatch');
  const nativeColorPicker = document.getElementById('nativeColorPicker');
  const palDots = document.querySelectorAll('.pal-dot');

  // 主工具按钮
  const toolBtns = document.querySelectorAll('.tool-btn[data-tool]');
  const btnUndo = document.getElementById('btnUndo');
  const btnRedo = document.getElementById('btnRedo');

  // 核心动作按钮
  const actAiEdit = document.getElementById('actAiEdit');
  const actOcr = document.getElementById('actOcr');
  const actCancel = document.getElementById('actCancel');
  const actPin = document.getElementById('actPin');
  const actSave = document.getElementById('actSave');
  const actConfirm = document.getElementById('actConfirm');

  // 2. 状态变量
  let stageWidth = 0;   // 逻辑宽度 (CSS px)
  let stageHeight = 0;  // 逻辑高度 (CSS px)
  let physicalW = 0;    // 物理分辨率
  let physicalH = 0;    // 物理分辨率
  let scaleRatioX = 1;  // 物理 / 逻辑 缩放比
  let scaleRatioY = 1;

  let fullSnapshotImg = null;
  let sourceOffscreenCanvas = null; // 1:1 物理像素纯净缓冲
  let sourceOffscreenCtx = null;
  let isStageLoaded = false;

  let activeTool = null; // 当前激活工具
  let activeColor = '#ef4444';
  let activeSize = 4;
  let isFilled = false;
  let isDashed = false;
  let arrowStyle = 'single';
  let blurMode = 'mosaic';
  let mosaicRadius = 10;
  let currentNumberCount = 1;
  let activeFontSize = 16;
  let isFontBold = true;
  let isFontItalic = false;
  let hasTextBg = false;

  // 绘图与拖拽
  let isDrawing = false;
  let drawStart = { x: 0, y: 0 };
  let brushPoints = [];
  let currentTextX = 0;
  let currentTextY = 0;

  // 窗口平移
  let isWindowMoving = false;
  let winDragStart = { x: 0, y: 0 };

  // 标注历史堆栈
  let annotHistory = [];
  let redoHistory = [];
  let recordedBoxes = [];
  let recordedTextNotes = [];

  /**
   * 3. 核心初始化：解析物理/逻辑像素并建立画布渲染
   */
  function initCaptureStage(initPayload) {
    if (!initPayload || !initPayload.dataUrl || isStageLoaded) return;
    isStageLoaded = true;

    const dataUrl = initPayload.dataUrl;
    const deviceScale = initPayload.scale || window.devicePixelRatio || 1;

    fullSnapshotImg = new Image();
    fullSnapshotImg.onload = () => {
      physicalW = initPayload.physW || fullSnapshotImg.naturalWidth;
      physicalH = initPayload.physH || fullSnapshotImg.naturalHeight;

      // 严格除以 DPI scaleFactor 得到屏幕实际逻辑尺寸，彻底解决自动放大全屏问题！
      stageWidth = initPayload.logicalW || Math.round(physicalW / deviceScale);
      stageHeight = initPayload.logicalH || Math.round(physicalH / deviceScale);

      scaleRatioX = physicalW / Math.max(1, stageWidth);
      scaleRatioY = physicalH / Math.max(1, stageHeight);

      // 设置舞台 CSS 尺寸
      stageContainer.style.width = stageWidth + 'px';
      stageContainer.style.height = stageHeight + 'px';

      // Canvas 像素宽度设为物理高清像素，CSS 尺寸设为逻辑像素
      bgCanvas.width = physicalW;
      bgCanvas.height = physicalH;
      bgCanvas.style.width = stageWidth + 'px';
      bgCanvas.style.height = stageHeight + 'px';

      annotCanvas.width = physicalW;
      annotCanvas.height = physicalH;
      annotCanvas.style.width = stageWidth + 'px';
      annotCanvas.style.height = stageHeight + 'px';

      // 离屏物理采样缓冲 (用于马赛克与干净导出)
      sourceOffscreenCanvas = document.createElement('canvas');
      sourceOffscreenCanvas.width = physicalW;
      sourceOffscreenCanvas.height = physicalH;
      sourceOffscreenCtx = sourceOffscreenCanvas.getContext('2d');
      sourceOffscreenCtx.drawImage(fullSnapshotImg, 0, 0, physicalW, physicalH);

      // 绘制背景底图
      bgCtx.drawImage(fullSnapshotImg, 0, 0, physicalW, physicalH);

      // 选区左上角尺寸标签
      sizeBadge.textContent = `${stageWidth} × ${stageHeight} px`;

      // 智能定位悬浮工具栏 (保证永不超出版面、永不丢失)
      updateToolbarPosition();

      clearAnnotations();
    };
    fullSnapshotImg.src = dataUrl;
  }

  // 暴露给主进程 IPC / executeJavaScript 调用
  window.loadFromInitData = function (payload) {
    initCaptureStage(payload);
  };

  // 兜底：从本地临时文件读取 (彻底根治 executeJavaScript 异步时序竞争)
  function tryAutoLoadFromDisk() {
    if (isStageLoaded) return;
    try {
      const fs = require('fs');
      const os = require('os');
      const path = require('path');
      const initFile = path.join(os.tmpdir(), 'rubick_capture_init.json');
      if (fs.existsSync(initFile)) {
        const text = fs.readFileSync(initFile, 'utf-8');
        const payload = JSON.parse(text);
        if (payload && payload.dataUrl) {
          initCaptureStage(payload);
        }
      }
    } catch (e) {
      console.warn('tryAutoLoadFromDisk 异常:', e.message);
    }
  }

  // DOM 就绪立即主动读盘尝试一次
  window.addEventListener('DOMContentLoaded', () => {
    tryAutoLoadFromDisk();
  });
  // 脚本载入立即也尝试一次
  tryAutoLoadFromDisk();

  /**
   * 4. 智能排布悬浮工具栏 (1:1 对齐 Snipaste：下方空间不足时翻入图片内侧右下角)
   */
  function updateToolbarPosition() {
    const tbHeight = subPalette.style.display === 'flex' ? 76 : 38;
    const winH = window.innerHeight || (stageHeight + 90);

    // 如果图片下方有足够空间放工具栏，停靠在图片外侧下方；
    // 如果图片几乎占满窗口或在屏幕边缘，智能翻转停靠在截图内部右下角，确保 100% 可见！
    if (winH - stageHeight >= tbHeight + 8) {
      toolbarWrap.style.top = (stageHeight + 6) + 'px';
      toolbarWrap.style.bottom = 'auto';
    } else {
      toolbarWrap.style.top = 'auto';
      toolbarWrap.style.bottom = '10px';
    }
    toolbarWrap.style.right = '6px';
  }

  window.addEventListener('resize', () => {
    updateToolbarPosition();
  });

  /**
   * 5. 绘图管线与高清 Canvas 渲染
   */
  function applyStrokeStyles(ctx, item) {
    ctx.strokeStyle = item.color;
    ctx.fillStyle = item.color;
    ctx.lineWidth = item.size;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    if (item.isDashed) {
      ctx.setLineDash([item.size * 2, item.size * 1.5]);
    } else {
      ctx.setLineDash([]);
    }
  }

  function renderActiveDrawing(curX, curY) {
    redrawAnnotations();

    annotCtx.save();
    annotCtx.scale(scaleRatioX, scaleRatioY);
    annotCtx.beginPath();
    annotCtx.rect(0, 0, stageWidth, stageHeight);
    annotCtx.clip();

    const tempItem = {
      color: activeColor,
      size: activeSize,
      isDashed,
      isFilled
    };
    applyStrokeStyles(annotCtx, tempItem);

    const minX = Math.min(drawStart.x, curX);
    const minY = Math.min(drawStart.y, curY);
    const w = Math.abs(curX - drawStart.x);
    const h = Math.abs(curY - drawStart.y);

    if (activeTool === 'rect') {
      if (isFilled) annotCtx.fillRect(minX, minY, w, h);
      else annotCtx.strokeRect(minX, minY, w, h);
    } else if (activeTool === 'ellipse') {
      annotCtx.beginPath();
      annotCtx.ellipse(minX + w / 2, minY + h / 2, w / 2, h / 2, 0, 0, Math.PI * 2);
      if (isFilled) annotCtx.fill();
      else annotCtx.stroke();
    } else if (activeTool === 'arrow') {
      drawArrow(annotCtx, drawStart.x, drawStart.y, curX, curY, activeSize, arrowStyle);
    } else if (activeTool === 'line') {
      annotCtx.beginPath();
      annotCtx.moveTo(drawStart.x, drawStart.y);
      annotCtx.lineTo(curX, curY);
      annotCtx.stroke();
    } else if (activeTool === 'brush') {
      brushPoints.push({ x: curX, y: curY });
      drawBrushPath(annotCtx, brushPoints, activeSize);
    } else if (activeTool === 'marker') {
      brushPoints.push({ x: curX, y: curY });
      annotCtx.save();
      annotCtx.globalAlpha = 0.35;
      drawBrushPath(annotCtx, brushPoints, Math.max(12, activeSize * 3));
      annotCtx.restore();
    } else if (activeTool === 'mosaic') {
      drawMosaic(annotCtx, minX, minY, w, h, mosaicRadius);
    } else if (activeTool === 'blur') {
      drawBlur(annotCtx, minX, minY, w, h, mosaicRadius);
    }

    annotCtx.restore();
  }

  function commitActiveDrawing(curX, curY) {
    const minX = Math.min(drawStart.x, curX);
    const minY = Math.min(drawStart.y, curY);
    const w = Math.abs(curX - drawStart.x);
    const h = Math.abs(curY - drawStart.y);

    let newEntry = null;

    if (activeTool === 'rect' && w > 4 && h > 4) {
      newEntry = { type: 'rect', color: activeColor, size: activeSize, isFilled, isDashed, x: minX, y: minY, w, h };
      recordedBoxes.push({ x: Math.round(minX), y: Math.round(minY), w: Math.round(w), h: Math.round(h) });
    } else if (activeTool === 'ellipse' && w > 4 && h > 4) {
      newEntry = { type: 'ellipse', color: activeColor, size: activeSize, isFilled, isDashed, x: minX, y: minY, w, h };
    } else if (activeTool === 'arrow' && (w > 4 || h > 4)) {
      newEntry = { type: 'arrow', color: activeColor, size: activeSize, isDashed, arrowStyle, x1: drawStart.x, y1: drawStart.y, x2: curX, y2: curY };
    } else if (activeTool === 'line' && (w > 4 || h > 4)) {
      newEntry = { type: 'line', color: activeColor, size: activeSize, isDashed, x1: drawStart.x, y1: drawStart.y, x2: curX, y2: curY };
    } else if (activeTool === 'brush' && brushPoints.length > 1) {
      newEntry = { type: 'brush', color: activeColor, size: activeSize, points: [...brushPoints] };
    } else if (activeTool === 'marker' && brushPoints.length > 1) {
      newEntry = { type: 'marker', color: activeColor, size: Math.max(12, activeSize * 3), points: [...brushPoints] };
    } else if (activeTool === 'mosaic' && w > 4 && h > 4) {
      newEntry = { type: 'mosaic', x: minX, y: minY, w, h, radius: mosaicRadius };
    } else if (activeTool === 'blur' && w > 4 && h > 4) {
      newEntry = { type: 'blur', x: minX, y: minY, w, h, radius: mosaicRadius };
    }

    if (newEntry) {
      annotHistory.push(newEntry);
      redoHistory = [];
    }

    brushPoints = [];
    redrawAnnotations();
  }

  function redrawAnnotations() {
    annotCtx.clearRect(0, 0, physicalW, physicalH);

    annotCtx.save();
    annotCtx.scale(scaleRatioX, scaleRatioY);
    annotCtx.beginPath();
    annotCtx.rect(0, 0, stageWidth, stageHeight);
    annotCtx.clip();

    for (const item of annotHistory) {
      annotCtx.save();
      applyStrokeStyles(annotCtx, item);

      if (item.type === 'rect') {
        if (item.isFilled) annotCtx.fillRect(item.x, item.y, item.w, item.h);
        else annotCtx.strokeRect(item.x, item.y, item.w, item.h);
      } else if (item.type === 'ellipse') {
        annotCtx.beginPath();
        annotCtx.ellipse(item.x + item.w / 2, item.y + item.h / 2, item.w / 2, item.h / 2, 0, 0, Math.PI * 2);
        if (item.isFilled) annotCtx.fill();
        else annotCtx.stroke();
      } else if (item.type === 'arrow') {
        drawArrow(annotCtx, item.x1, item.y1, item.x2, item.y2, item.size, item.arrowStyle);
      } else if (item.type === 'line') {
        annotCtx.beginPath();
        annotCtx.moveTo(item.x1, item.y1);
        annotCtx.lineTo(item.x2, item.y2);
        annotCtx.stroke();
      } else if (item.type === 'brush') {
        drawBrushPath(annotCtx, item.points, item.size);
      } else if (item.type === 'marker') {
        annotCtx.globalAlpha = 0.35;
        drawBrushPath(annotCtx, item.points, item.size);
      } else if (item.type === 'mosaic') {
        drawMosaic(annotCtx, item.x, item.y, item.w, item.h, item.radius);
      } else if (item.type === 'blur') {
        drawBlur(annotCtx, item.x, item.y, item.w, item.h, item.radius);
      } else if (item.type === 'number') {
        drawNumberBadge(annotCtx, item.x, item.y, item.num, item.color);
      } else if (item.type === 'text') {
        annotCtx.textBaseline = 'top';
        annotCtx.font = `${item.italic ? 'italic ' : ''}${item.bold ? 'bold ' : ''}${item.fontSize || 16}px sans-serif`;
        if (item.hasBg) {
          const txtW = annotCtx.measureText(item.text).width;
          annotCtx.fillStyle = 'rgba(0, 0, 0, 0.72)';
          annotCtx.fillRect(item.x - 3, item.y - 2, txtW + 6, (item.fontSize || 16) + 4);
        }
        annotCtx.fillStyle = item.color;
        annotCtx.fillText(item.text, item.x, item.y);
      }

      annotCtx.restore();
    }

    annotCtx.restore();
  }

  function clearAnnotations() {
    annotHistory = [];
    redoHistory = [];
    recordedBoxes = [];
    recordedTextNotes = [];
    annotCtx.clearRect(0, 0, physicalW, physicalH);
  }

  function drawArrow(ctx, x1, y1, x2, y2, size, style = 'single') {
    const headLen = Math.max(12, size * 3.5);
    const angle = Math.atan2(y2 - y1, x2 - x1);

    ctx.beginPath();
    ctx.moveTo(x1, y1);
    ctx.lineTo(x2, y2);
    ctx.stroke();

    ctx.save();
    ctx.fillStyle = ctx.strokeStyle;
    ctx.beginPath();
    ctx.moveTo(x2, y2);
    ctx.lineTo(x2 - headLen * Math.cos(angle - Math.PI / 7), y2 - headLen * Math.sin(angle - Math.PI / 7));
    ctx.lineTo(x2 - (headLen * 0.7) * Math.cos(angle), y2 - (headLen * 0.7) * Math.sin(angle));
    ctx.lineTo(x2 - headLen * Math.cos(angle + Math.PI / 7), y2 - headLen * Math.sin(angle + Math.PI / 7));
    ctx.closePath();
    ctx.fill();

    if (style === 'double') {
      const oppAngle = angle + Math.PI;
      ctx.beginPath();
      ctx.moveTo(x1, y1);
      ctx.lineTo(x1 - headLen * Math.cos(oppAngle - Math.PI / 7), y1 - headLen * Math.sin(oppAngle - Math.PI / 7));
      ctx.lineTo(x1 - (headLen * 0.7) * Math.cos(oppAngle), y1 - (headLen * 0.7) * Math.sin(oppAngle));
      ctx.lineTo(x1 - headLen * Math.cos(oppAngle + Math.PI / 7), y1 - headLen * Math.sin(oppAngle + Math.PI / 7));
      ctx.closePath();
      ctx.fill();
    }
    ctx.restore();
  }

  function drawBrushPath(ctx, points, size) {
    if (points.length < 2) return;
    ctx.lineWidth = size;
    ctx.beginPath();
    ctx.moveTo(points[0].x, points[0].y);
    for (let i = 1; i < points.length; i++) {
      ctx.lineTo(points[i].x, points[i].y);
    }
    ctx.stroke();
  }

  /**
   * 6. 马赛克 (Pixelate) 算法：精准采样，彻底防越界与异常
   */
  function drawMosaic(ctx, mx, my, mw, mh, radius = 8) {
    mx = Math.round(mx);
    my = Math.round(my);
    mw = Math.round(mw);
    mh = Math.round(mh);
    if (mw < 2 || mh < 2 || !sourceOffscreenCtx) return;

    const blockSize = Math.max(4, Math.round(radius));
    const safeW = Math.min(mw, stageWidth - mx);
    const safeH = Math.min(mh, stageHeight - my);
    if (safeW <= 0 || safeH <= 0) return;

    try {
      const pMx = Math.round(mx * scaleRatioX);
      const pMy = Math.round(my * scaleRatioY);
      const pMw = Math.round(safeW * scaleRatioX);
      const pMh = Math.round(safeH * scaleRatioY);
      if (pMw <= 0 || pMh <= 0) return;

      const imgData = sourceOffscreenCtx.getImageData(pMx, pMy, pMw, pMh);
      const data = imgData.data;

      for (let y = 0; y < safeH; y += blockSize) {
        for (let x = 0; x < safeW; x += blockSize) {
          const samplePx = Math.min(Math.floor(x * scaleRatioX), pMw - 1);
          const samplePy = Math.min(Math.floor(y * scaleRatioY), pMh - 1);
          const pIndex = (samplePy * pMw + samplePx) * 4;
          const r = data[pIndex];
          const g = data[pIndex + 1];
          const b = data[pIndex + 2];
          ctx.fillStyle = `rgb(${r},${g},${b})`;
          ctx.fillRect(mx + x, my + y, Math.min(blockSize, safeW - x), Math.min(blockSize, safeH - y));
        }
      }
    } catch (err) {
      console.error('drawMosaic 执行异常:', err);
    }
  }

  /**
   * 7. 高斯模糊 (Smooth Blur) 算法
   */
  function drawBlur(ctx, mx, my, mw, mh, radius = 8) {
    mx = Math.round(mx);
    my = Math.round(my);
    mw = Math.round(mw);
    mh = Math.round(mh);
    if (mw < 2 || mh < 2) return;

    try {
      ctx.save();
      ctx.beginPath();
      ctx.rect(mx, my, mw, mh);
      ctx.clip();
      ctx.filter = `blur(${Math.max(2, Math.round(radius / 2))}px)`;
      ctx.drawImage(bgCanvas, 0, 0, physicalW, physicalH, 0, 0, stageWidth, stageHeight);
      ctx.restore();
    } catch (err) {
      console.error('drawBlur 执行异常:', err);
    }
  }

  /**
   * 8. 序号标记
   */
  function addNumberBadge(x, y) {
    annotHistory.push({
      type: 'number',
      x, y,
      num: currentNumberCount++,
      color: activeColor
    });
    redoHistory = [];
    redrawAnnotations();
  }

  function drawNumberBadge(ctx, x, y, num, color) {
    const r = 12;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fillStyle = color;
    ctx.fill();
    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 1.5;
    ctx.stroke();

    ctx.fillStyle = '#ffffff';
    ctx.font = 'bold 11px sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(num, x, y + 0.5);
  }

  /**
   * 9. 橡皮擦 (点选命中消除)
   */
  function eraseAtPoint(x, y) {
    for (let i = annotHistory.length - 1; i >= 0; i--) {
      const it = annotHistory[i];
      let hit = false;
      if (it.type === 'rect' || it.type === 'ellipse' || it.type === 'mosaic' || it.type === 'blur') {
        hit = x >= it.x - 4 && x <= it.x + it.w + 4 && y >= it.y - 4 && y <= it.y + it.h + 4;
      } else if (it.type === 'number') {
        hit = Math.hypot(x - it.x, y - it.y) <= 15;
      } else if (it.type === 'text') {
        hit = x >= it.x - 4 && x <= it.x + 120 && y >= it.y - 4 && y <= it.y + (it.fontSize || 16) + 4;
      } else if (it.type === 'brush' || it.type === 'marker') {
        hit = it.points.some(p => Math.hypot(x - p.x, y - p.y) <= Math.max(8, it.size));
      } else if (it.type === 'arrow' || it.type === 'line') {
        hit = Math.hypot(x - it.x1, y - it.y1) + Math.hypot(x - it.x2, y - it.y2) - Math.hypot(it.x1 - it.x2, it.y1 - it.y2) < 4;
      }

      if (hit) {
        annotHistory.splice(i, 1);
        redrawAnnotations();
        break;
      }
    }
  }

  /**
   * 10. 文字输入标注与提交闭环
   */
  function activateTextInput(x, y) {
    commitTextInput();

    currentTextX = Math.round(x);
    currentTextY = Math.round(y);

    inlineTextInput.style.color = activeColor;
    inlineTextInput.style.fontSize = activeFontSize + 'px';
    inlineTextInput.style.fontWeight = isFontBold ? 'bold' : 'normal';
    inlineTextInput.style.fontStyle = isFontItalic ? 'italic' : 'normal';
    inlineTextInput.style.left = Math.min(stageWidth - 120, Math.max(4, currentTextX)) + 'px';
    inlineTextInput.style.top = Math.min(stageHeight - 36, Math.max(4, currentTextY)) + 'px';
    inlineTextInput.value = '';
    inlineTextInput.style.display = 'block';

    setTimeout(() => {
      inlineTextInput.focus();
    }, 30);
  }

  function commitTextInput() {
    if (inlineTextInput.style.display !== 'block') return;
    const val = inlineTextInput.value.trim();
    if (val) {
      annotHistory.push({
        type: 'text',
        text: val,
        x: currentTextX,
        y: currentTextY,
        color: activeColor,
        fontSize: activeFontSize,
        bold: isFontBold,
        italic: isFontItalic,
        hasBg: hasTextBg
      });
      recordedTextNotes.push(val);
      redoHistory = [];
      redrawAnnotations();
    }
    inlineTextInput.value = '';
    inlineTextInput.style.display = 'none';
  }

  inlineTextInput.addEventListener('blur', () => {
    setTimeout(commitTextInput, 60);
  });
  inlineTextInput.addEventListener('keydown', (ev) => {
    if (ev.key === 'Enter' && !ev.shiftKey) {
      ev.preventDefault();
      commitTextInput();
    } else if (ev.key === 'Escape') {
      ev.preventDefault();
      inlineTextInput.value = '';
      inlineTextInput.style.display = 'none';
    }
  });

  /**
   * 11. 鼠标事件交互中心
   */
  stageContainer.addEventListener('mousedown', (e) => {
    if (e.button === 2) {
      handleRightClick();
      return;
    }
    if (e.target.closest('#inlineTextInput')) return;

    const rect = stageContainer.getBoundingClientRect();
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;

    // A. 橡皮擦模式
    if (activeTool === 'eraser') {
      eraseAtPoint(x, y);
      return;
    }

    // B. 序号模式
    if (activeTool === 'number') {
      addNumberBadge(x, y);
      return;
    }

    // C. 打字模式
    if (activeTool === 'text') {
      activateTextInput(x, y);
      return;
    }

    // D. 标注绘图模式
    if (activeTool) {
      commitTextInput();
      isDrawing = true;
      drawStart = { x, y };
      if (activeTool === 'brush' || activeTool === 'marker') {
        brushPoints = [{ x, y }];
      }
      return;
    }

    // E. 无激活工具：允许按住截图整体平移窗口
    isWindowMoving = true;
    winDragStart = { x: e.screenX, y: e.screenY };
  });

  window.addEventListener('mousemove', (e) => {
    if (isWindowMoving) {
      const dx = e.screenX - winDragStart.x;
      const dy = e.screenY - winDragStart.y;
      winDragStart = { x: e.screenX, y: e.screenY };
      window.moveBy(dx, dy);
      return;
    }

    if (isDrawing && activeTool) {
      const rect = stageContainer.getBoundingClientRect();
      const x = Math.max(0, Math.min(stageWidth, e.clientX - rect.left));
      const y = Math.max(0, Math.min(stageHeight, e.clientY - rect.top));
      renderActiveDrawing(x, y);
    }
  });

  window.addEventListener('mouseup', (e) => {
    if (isWindowMoving) {
      isWindowMoving = false;
      return;
    }

    if (isDrawing) {
      isDrawing = false;
      const rect = stageContainer.getBoundingClientRect();
      const x = Math.max(0, Math.min(stageWidth, e.clientX - rect.left));
      const y = Math.max(0, Math.min(stageHeight, e.clientY - rect.top));
      commitActiveDrawing(x, y);
    }
  });

  // 鼠标滚轮调节粗细 / 颗粒度
  window.addEventListener('wheel', (e) => {
    e.preventDefault();
    const delta = e.deltaY < 0 ? 1 : -1;

    if (activeTool === 'mosaic' || activeTool === 'blur') {
      mosaicRadius = Math.max(4, Math.min(32, mosaicRadius + delta * 2));
      rngMosaicSize.value = mosaicRadius;
      valMosaicSize.textContent = mosaicRadius;
      subParamBadge.textContent = mosaicRadius + 'px';
    } else {
      activeSize = Math.max(1, Math.min(64, activeSize + delta));
      subParamBadge.textContent = activeSize + 'px';
    }
  }, { passive: false });

  // 右键三级取消
  function handleRightClick() {
    if (inlineTextInput.style.display === 'block') {
      inlineTextInput.value = '';
      inlineTextInput.style.display = 'none';
      return;
    }
    if (isDrawing) {
      isDrawing = false;
      brushPoints = [];
      redrawAnnotations();
      return;
    }
    if (activeTool) {
      activeTool = null;
      toolBtns.forEach(b => b.classList.remove('active'));
      subPalette.style.display = 'none';
      stageContainer.style.cursor = 'move';
      updateToolbarPosition();
      return;
    }
    finishWithAction('cancel');
  }

  /**
   * 12. 工具栏与属性栏控制
   */
  toolBtns.forEach(btn => {
    btn.addEventListener('click', () => {
      const tool = btn.dataset.tool;
      commitTextInput();

      if (activeTool === tool) {
        if (tool === 'rect') {
          activeTool = 'ellipse';
          btn.dataset.tool = 'ellipse';
          btn.title = '椭圆 (E)';
        } else if (tool === 'ellipse') {
          activeTool = 'rect';
          btn.dataset.tool = 'rect';
          btn.title = '矩形 (R)';
        } else if (tool === 'arrow') {
          activeTool = 'line';
          btn.dataset.tool = 'line';
          btn.title = '直线 (L)';
        } else if (tool === 'line') {
          activeTool = 'arrow';
          btn.dataset.tool = 'arrow';
          btn.title = '箭头 (A)';
        } else if (tool === 'mosaic') {
          activeTool = 'blur';
          btn.dataset.tool = 'blur';
          btn.title = '高斯模糊 (G)';
        } else if (tool === 'blur') {
          activeTool = 'mosaic';
          btn.dataset.tool = 'mosaic';
          btn.title = '马赛克 (K)';
        }
      } else {
        activeTool = tool;
      }

      toolBtns.forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      stageContainer.style.cursor = activeTool === 'eraser' ? 'cell' : 'crosshair';

      showSubPaletteForTool(activeTool);
      updateToolbarPosition();
    });
  });

  function showSubPaletteForTool(tool) {
    subPalette.style.display = 'flex';
    subShapeOptions.style.display = 'none';
    subLineOptions.style.display = 'none';
    subMosaicOptions.style.display = 'none';
    subNumberOptions.style.display = 'none';
    subTextOptions.style.display = 'none';

    if (tool === 'rect' || tool === 'ellipse') {
      subShapeOptions.style.display = 'flex';
      subParamBadge.textContent = activeSize + 'px';
    } else if (tool === 'arrow' || tool === 'line') {
      subLineOptions.style.display = 'flex';
      subParamBadge.textContent = activeSize + 'px';
    } else if (tool === 'brush') {
      subParamBadge.textContent = activeSize + 'px';
    } else if (tool === 'marker') {
      subParamBadge.textContent = Math.max(12, activeSize * 3) + 'px';
    } else if (tool === 'mosaic' || tool === 'blur') {
      subMosaicOptions.style.display = 'flex';
      blurModeLabel.textContent = tool === 'blur' ? '平滑模糊' : '马赛克';
      subParamBadge.textContent = mosaicRadius + 'px';
    } else if (tool === 'number') {
      subNumberOptions.style.display = 'flex';
      subParamBadge.textContent = '#' + currentNumberCount;
    } else if (tool === 'text') {
      subTextOptions.style.display = 'flex';
      subParamBadge.textContent = activeFontSize + 'px';
    } else if (tool === 'eraser') {
      subPalette.style.display = 'none';
    }
  }

  // 撤销 / 重做
  btnUndo.addEventListener('click', () => {
    commitTextInput();
    if (annotHistory.length > 0) {
      redoHistory.push(annotHistory.pop());
      redrawAnnotations();
    }
  });

  btnRedo.addEventListener('click', () => {
    commitTextInput();
    if (redoHistory.length > 0) {
      annotHistory.push(redoHistory.pop());
      redrawAnnotations();
    }
  });

  // 形状：填充开关
  btnToggleFill.addEventListener('click', () => {
    isFilled = !isFilled;
    btnToggleFill.classList.toggle('active', isFilled);
    fillIconInner.setAttribute('fill', isFilled ? 'currentColor' : 'none');
  });

  // 形状/线条：虚线开关
  btnToggleDash.addEventListener('click', () => {
    isDashed = !isDashed;
    btnToggleDash.classList.toggle('active', isDashed);
  });

  // 线条：箭头类型切换
  btnArrowStyle.addEventListener('click', () => {
    if (arrowStyle === 'single') {
      arrowStyle = 'double';
      arrowStyleLabel.textContent = '双向 ↔';
    } else if (arrowStyle === 'double') {
      arrowStyle = 'line';
      arrowStyleLabel.textContent = '直线 —';
    } else {
      arrowStyle = 'single';
      arrowStyleLabel.textContent = '单向 ↗';
    }
  });

  // 马赛克模式切换
  btnToggleBlurMode.addEventListener('click', () => {
    blurMode = blurMode === 'mosaic' ? 'blur' : 'mosaic';
    activeTool = blurMode;
    blurModeLabel.textContent = blurMode === 'blur' ? '平滑模糊' : '马赛克';
  });

  // 马赛克滑杆
  rngMosaicSize.addEventListener('input', () => {
    mosaicRadius = parseInt(rngMosaicSize.value, 10);
    valMosaicSize.textContent = mosaicRadius;
    subParamBadge.textContent = mosaicRadius + 'px';
  });

  // 序号重置
  btnResetNumber.addEventListener('click', () => {
    currentNumberCount = 1;
    subParamBadge.textContent = '#1';
    btnResetNumber.querySelector('span').textContent = '已重置 ①';
    setTimeout(() => {
      btnResetNumber.querySelector('span').textContent = '序号重置 ①';
    }, 1200);
  });

  // 文字属性
  btnTextBold.addEventListener('click', () => {
    isFontBold = !isFontBold;
    btnTextBold.classList.toggle('active', isFontBold);
  });
  btnTextItalic.addEventListener('click', () => {
    isFontItalic = !isFontItalic;
    btnTextItalic.classList.toggle('active', isFontItalic);
  });
  btnTextBg.addEventListener('click', () => {
    hasTextBg = !hasTextBg;
    btnTextBg.classList.toggle('active', hasTextBg);
  });
  selFontSize.addEventListener('change', () => {
    activeFontSize = parseInt(selFontSize.value, 10) || 16;
    subParamBadge.textContent = activeFontSize + 'px';
  });

  // 20 色调色板选择
  palDots.forEach(dot => {
    dot.addEventListener('click', () => {
      palDots.forEach(d => d.classList.remove('active'));
      dot.classList.add('active');
      activeColor = dot.dataset.color;
      currentSwatch.style.backgroundColor = activeColor;
      nativeColorPicker.value = activeColor;
      if (inlineTextInput.style.display === 'block') {
        inlineTextInput.style.color = activeColor;
      }
    });
  });

  nativeColorPicker.addEventListener('input', (e) => {
    activeColor = e.target.value;
    currentSwatch.style.backgroundColor = activeColor;
    palDots.forEach(d => d.classList.remove('active'));
  });

  /**
   * 13. 产物导出与业务触发 (独家三图分离架构，物理分辨率无损导出)
   */
  function exportCaptureData() {
    commitTextInput();
    if (!physicalW || !physicalH) return null;

    // 1. 干净底图 (Clean)
    const cleanCanvas = document.createElement('canvas');
    cleanCanvas.width = physicalW;
    cleanCanvas.height = physicalH;
    const cCtx = cleanCanvas.getContext('2d');
    cCtx.drawImage(bgCanvas, 0, 0);
    const cleanDataUrl = cleanCanvas.toDataURL('image/png');

    // 2. 带涂鸦的完整合成图 (Annotated)
    const annotCanvasMerged = document.createElement('canvas');
    annotCanvasMerged.width = physicalW;
    annotCanvasMerged.height = physicalH;
    const aCtx = annotCanvasMerged.getContext('2d');
    aCtx.drawImage(bgCanvas, 0, 0);
    aCtx.drawImage(annotCanvas, 0, 0);
    const annotatedDataUrl = annotCanvasMerged.toDataURL('image/png');

    // 3. 构建三图分离包
    const bundle = {
      cleanDataUrl,
      annotatedDataUrl,
      boxes: recordedBoxes,
      textNotes: recordedTextNotes,
      hasMaskRegion: recordedBoxes.length > 0,
      timestamp: Date.now()
    };

    return { cleanDataUrl, annotatedDataUrl, bundle };
  }

  function finishWithAction(actionType) {
    const data = exportCaptureData();
    if (!data && actionType !== 'cancel') return;

    if (actionType === 'copy' && data && (data.annotatedDataUrl || data.cleanDataUrl)) {
      const copyUrl = data.annotatedDataUrl || data.cleanDataUrl;
      try {
        if (window.utools && typeof window.utools.copyImage === 'function') {
          window.utools.copyImage(copyUrl);
        }
      } catch (e) {}
    }

    try {
      const fs = require('fs');
      const os = require('os');
      const path = require('path');
      const CAPTURE_IPC_FILE = path.join(os.tmpdir(), 'rubick_capture_ipc_result.json');
      fs.writeFileSync(CAPTURE_IPC_FILE, JSON.stringify({
        action: actionType,
        data: data,
        timestamp: Date.now()
      }), 'utf-8');
    } catch (e) {}

    window.close();
  }

  // 按钮事件绑定
  actAiEdit.addEventListener('click', () => finishWithAction('ai_edit'));
  actOcr.addEventListener('click', () => finishWithAction('ocr'));
  actPin.addEventListener('click', () => finishWithAction('pin'));
  actSave.addEventListener('click', () => {
    const data = exportCaptureData();
    if (!data) return;
    const a = document.createElement('a');
    a.href = data.annotatedDataUrl || data.cleanDataUrl;
    a.download = `Capture_${Date.now()}.png`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    finishWithAction('copy');
  });
  actCancel.addEventListener('click', () => finishWithAction('cancel'));
  actConfirm.addEventListener('click', () => finishWithAction('copy'));

  // 双击截图直接复制并关闭
  stageContainer.addEventListener('dblclick', () => finishWithAction('copy'));

  // 键盘快捷键 (1:1 对齐 Snipaste)
  window.addEventListener('keydown', (e) => {
    if (inlineTextInput.style.display === 'block') return;

    if (e.key === 'Escape') {
      handleRightClick();
    } else if (e.key === 'Enter') {
      finishWithAction('copy');
    } else if (e.key === 'F3') {
      finishWithAction('pin');
    } else if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')) {
      e.preventDefault();
      actSave.click();
    } else if ((e.ctrlKey || e.metaKey) && (e.key === 'z' || e.key === 'Z')) {
      e.preventDefault();
      btnUndo.click();
    } else if ((e.ctrlKey || e.metaKey) && (e.key === 'y' || e.key === 'Y')) {
      e.preventDefault();
      btnRedo.click();
    } else if (!e.ctrlKey && !e.metaKey && !e.altKey) {
      const k = e.key.toLowerCase();
      if (k === 'r') document.getElementById('toolShape').click();
      else if (k === 'e') { activeTool = 'ellipse'; document.getElementById('toolShape').click(); }
      else if (k === 'a') document.getElementById('toolLineArrow').click();
      else if (k === 'l') { activeTool = 'line'; document.getElementById('toolLineArrow').click(); }
      else if (k === 'b') document.getElementById('toolBrush').click();
      else if (k === 'm') document.getElementById('toolMarker').click();
      else if (k === 'k') document.getElementById('toolMosaic').click();
      else if (k === 'g') { activeTool = 'blur'; document.getElementById('toolMosaic').click(); }
      else if (k === 't') document.getElementById('toolText').click();
      else if (k === 'n') document.getElementById('toolNumber').click();
      else if (k === 'x') document.getElementById('toolEraser').click();
    }
  });

})();
