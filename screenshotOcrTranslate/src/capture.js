/**
 * 纯 JS 原生级全功能截屏引擎与全能悬浮工具栏 (100% 对齐 Snipaste 功能生态)
 * 零二进制依赖，100% 遵守 uTools 应用市场审核红线
 * 包含：8大标注工具、荧光笔、序号标注、橡皮擦、撤销重做、滚轮调节笔宽、20色调色板、三图分离
 */

(function () {
  // 核心 DOM 节点
  const stageContainer = document.getElementById('stageContainer');
  const bgCanvas = document.getElementById('bgCanvas');
  const annotCanvas = document.getElementById('annotCanvas');
  const bgCtx = bgCanvas.getContext('2d');
  const annotCtx = annotCanvas.getContext('2d');

  const selectionBox = document.getElementById('selectionBox');
  const sizeBadge = document.getElementById('sizeBadge');
  const magnifier = document.getElementById('magnifier');
  const magCanvas = document.getElementById('magCanvas');
  const magCtx = magCanvas.getContext('2d');
  const magColorSample = document.getElementById('magColorSample');
  const magHexText = document.getElementById('magHexText');
  const magRgbText = document.getElementById('magRgbText');

  const toolbarWrap = document.getElementById('toolbarWrap');
  const floatingToolbar = document.getElementById('floatingToolbar');
  const subPalette = document.getElementById('subPalette');
  const subParamBadge = document.getElementById('subParamBadge');
  const inlineTextInput = document.getElementById('inlineTextInput');

  // 二级选项分区
  const subShapeOptions = document.getElementById('subShapeOptions');
  const btnToggleFill = document.getElementById('btnToggleFill');
  const fillIconInner = document.getElementById('fillIconInner');
  const btnToggleDash = document.getElementById('btnToggleDash');

  const subLineOptions = document.getElementById('subLineOptions');
  const btnArrowStyle = document.getElementById('btnArrowStyle');
  const arrowStyleLabel = document.getElementById('arrowStyleLabel');

  const subMosaicOptions = document.getElementById('subMosaicOptions');
  const btnToggleBlurMode = document.getElementById('btnToggleBlurMode');
  const blurModeLabel = document.getElementById('blurModeLabel');
  const rngMosaicSize = document.getElementById('rngMosaicSize');
  const valMosaicSize = document.getElementById('valMosaicSize');

  const subNumberOptions = document.getElementById('subNumberOptions');
  const btnResetNumber = document.getElementById('btnResetNumber');

  const subTextOptions = document.getElementById('subTextOptions');
  const btnTextBold = document.getElementById('btnTextBold');
  const btnTextItalic = document.getElementById('btnTextItalic');
  const btnTextBg = document.getElementById('btnTextBg');
  const selFontSize = document.getElementById('selFontSize');

  const currentSwatch = document.getElementById('currentSwatch');
  const nativeColorPicker = document.getElementById('nativeColorPicker');
  const palDots = document.querySelectorAll('.pal-dot');

  // 工具栏按钮
  const toolBtns = document.querySelectorAll('.tool-btn[data-tool]');
  const btnUndo = document.getElementById('btnUndo');
  const btnRedo = document.getElementById('btnRedo');
  const actAiEdit = document.getElementById('actAiEdit');
  const actOcr = document.getElementById('actOcr');
  const actPin = document.getElementById('actPin');
  const actSave = document.getElementById('actSave');
  const actCancel = document.getElementById('actCancel');
  const actConfirm = document.getElementById('actConfirm');

  // 状态机与配置
  let fullSnapshotImg = new Image();
  let imgLoaded = false;
  let screenWidth = window.innerWidth;
  let screenHeight = window.innerHeight;
  let dpr = window.devicePixelRatio || 1;
  let isRegionMode = false;

  // 选区几何
  let selRect = { x: 0, y: 0, w: 0, h: 0 };
  let isSelecting = false;
  let isSelected = false;
  let isResizing = false;
  let isMoving = false;
  let activeHandle = null;
  let dragStart = { x: 0, y: 0 };
  let origRect = { x: 0, y: 0, w: 0, h: 0 };

  // 当前标注全局属性
  let activeTool = null; // 'rect' | 'ellipse' | 'arrow' | 'line' | 'brush' | 'marker' | 'mosaic' | 'blur' | 'text' | 'number' | 'eraser' | null
  let activeColor = '#ef4444';
  let activeSize = 4;
  let isFilled = false;
  let isDashed = false;
  let arrowStyle = 'single'; // 'single' | 'double' | 'line'
  let blurMode = 'mosaic'; // 'mosaic' | 'blur'
  let mosaicRadius = 10;
  let currentNumberCount = 1;

  let isFontBold = true;
  let isFontItalic = false;
  let hasTextBg = false;
  let activeFontSize = 16;

  let isDrawing = false;
  let drawStart = { x: 0, y: 0 };
  let brushPoints = [];

  // 标注历史栈与重做栈
  let annotHistory = [];
  let redoHistory = [];
  let recordedBoxes = []; // 供 AI 改图继承的红框坐标
  let recordedTextNotes = []; // 供 AI 改图继承的文字指令

  /**
   * 1. 初始化屏幕与画布
   */
  function initScreen() {
    screenWidth = window.innerWidth;
    screenHeight = window.innerHeight;
    dpr = window.devicePixelRatio || 1;

    bgCanvas.width = screenWidth * dpr;
    bgCanvas.height = screenHeight * dpr;
    bgCanvas.style.width = screenWidth + 'px';
    bgCanvas.style.height = screenHeight + 'px';
    bgCtx.scale(dpr, dpr);

    annotCanvas.width = screenWidth * dpr;
    annotCanvas.height = screenHeight * dpr;
    annotCanvas.style.width = screenWidth + 'px';
    annotCanvas.style.height = screenHeight + 'px';
    annotCtx.scale(dpr, dpr);
  }

  window.addEventListener('resize', initScreen);
  initScreen();

  /**
   * 载入全屏快照底图并初次绘制
   */
  window.loadScreenSnapshot = function (dataUrl, options = {}) {
    if (!dataUrl) return;
    fullSnapshotImg.onload = () => {
      imgLoaded = true;
      const nw = fullSnapshotImg.naturalWidth;
      const nh = fullSnapshotImg.naturalHeight;

      if (options.isRegion || (nw < screenWidth * 0.95 || nh < screenHeight * 0.95)) {
        isRegionMode = true;
        // 选区模式：居中呈现选区
        const rw = Math.min(nw, screenWidth - 40);
        const rh = Math.min(nh, screenHeight - 80);
        const rx = Math.max(10, Math.floor((screenWidth - rw) / 2));
        const ry = Math.max(10, Math.floor((screenHeight - rh) / 2));

        selRect = { x: rx, y: ry, w: rw, h: rh };
        isSelected = true;
        magnifier.style.display = 'none';

        renderBackground();
        updateSelectionUI();
      } else {
        isRegionMode = false;
        renderBackground();
        magnifier.style.display = 'flex';
      }
    };
    fullSnapshotImg.src = dataUrl;
  };

  try {
    const cachedSnap = sessionStorage.getItem('rubick_screen_snapshot');
    if (cachedSnap) {
      window.loadScreenSnapshot(cachedSnap);
      sessionStorage.removeItem('rubick_screen_snapshot');
    }
  } catch (e) {}

  /**
   * 2. 绘制背景与掏空选区
   */
  function renderBackground() {
    if (!imgLoaded) return;
    bgCtx.clearRect(0, 0, screenWidth, screenHeight);

    if (isRegionMode) {
      // 选区模式：暗色半透明底，居中绘制选区图片
      bgCtx.fillStyle = 'rgba(0, 0, 0, 0.42)';
      bgCtx.fillRect(0, 0, screenWidth, screenHeight);

      if (selRect.w > 0 && selRect.h > 0) {
        bgCtx.drawImage(fullSnapshotImg, selRect.x, selRect.y, selRect.w, selRect.h);
      }
    } else {
      // 全屏模式：铺满底图，盖暗色层，掏空高亮
      bgCtx.drawImage(fullSnapshotImg, 0, 0, screenWidth, screenHeight);
      bgCtx.fillStyle = 'rgba(0, 0, 0, 0.42)';
      bgCtx.fillRect(0, 0, screenWidth, screenHeight);

      if (selRect.w > 0 && selRect.h > 0) {
        bgCtx.save();
        bgCtx.beginPath();
        bgCtx.rect(selRect.x, selRect.y, selRect.w, selRect.h);
        bgCtx.clip();
        bgCtx.drawImage(fullSnapshotImg, 0, 0, screenWidth, screenHeight);
        bgCtx.restore();
      }
    }
  }

  /**
   * 3. 像素放大镜与取色器
   */
  function updateMagnifier(x, y) {
    if (isSelected || !imgLoaded) {
      magnifier.style.display = 'none';
      return;
    }

    magnifier.style.display = 'flex';
    let magX = x + 18;
    let magY = y + 18;
    if (magX + 140 > screenWidth) magX = x - 148;
    if (magY + 160 > screenHeight) magY = y - 168;

    magnifier.style.left = magX + 'px';
    magnifier.style.top = magY + 'px';

    const sampleSize = 15;
    const magW = magCanvas.width;
    const magH = magCanvas.height;

    magCtx.imageSmoothingEnabled = false;
    magCtx.clearRect(0, 0, magW, magH);

    magCtx.drawImage(
      fullSnapshotImg,
      (x - Math.floor(sampleSize / 2)) * (fullSnapshotImg.naturalWidth / screenWidth),
      (y - Math.floor(sampleSize / 2)) * (fullSnapshotImg.naturalHeight / screenHeight),
      sampleSize * (fullSnapshotImg.naturalWidth / screenWidth),
      sampleSize * (fullSnapshotImg.naturalHeight / screenHeight),
      0, 0, magW, magH
    );

    // 中心红色准星十字
    magCtx.strokeStyle = 'rgba(239, 68, 68, 0.9)';
    magCtx.lineWidth = 1.5;
    magCtx.beginPath();
    magCtx.moveTo(magW / 2, 0); magCtx.lineTo(magW / 2, magH);
    magCtx.moveTo(0, magH / 2); magCtx.lineTo(magW, magH / 2);
    magCtx.stroke();

    try {
      const p = magCtx.getImageData(Math.floor(magW / 2), Math.floor(magH / 2), 1, 1).data;
      const hex = rgbToHex(p[0], p[1], p[2]);
      magColorSample.style.backgroundColor = hex;
      magHexText.textContent = hex;
      magRgbText.textContent = `RGB(${p[0]}, ${p[1]}, ${p[2]})`;
      magnifier.dataset.currentColor = hex;
    } catch (e) {}
  }

  function rgbToHex(r, g, b) {
    return '#' + [r, g, b].map(x => {
      const h = x.toString(16);
      return h.length === 1 ? '0' + h : h;
    }).join('').toUpperCase();
  }

  /**
   * 4. 选区与悬浮工具栏智能定位
   */
  function updateSelectionUI() {
    if (selRect.w <= 0 || selRect.h <= 0) {
      selectionBox.style.display = 'none';
      toolbarWrap.style.display = 'none';
      return;
    }

    selectionBox.style.display = 'block';
    selectionBox.style.left = selRect.x + 'px';
    selectionBox.style.top = selRect.y + 'px';
    selectionBox.style.width = selRect.w + 'px';
    selectionBox.style.height = selRect.h + 'px';

    sizeBadge.textContent = `${Math.round(selRect.w)} × ${Math.round(selRect.h)} px`;

    updateToolbarPosition();
  }

  function updateToolbarPosition() {
    if (!isSelected) {
      toolbarWrap.style.display = 'none';
      return;
    }

    toolbarWrap.style.display = 'flex';
    const tbWidth = 510;
    const tbHeight = subPalette.style.display === 'flex' ? 76 : 38;

    let left = selRect.x + selRect.w - tbWidth;
    if (left + tbWidth > screenWidth - 8) left = screenWidth - tbWidth - 8;
    if (left < 8) left = 8;

    let top = selRect.y + selRect.h + 8;
    if (top + tbHeight > screenHeight - 8) {
      if (selRect.y - tbHeight - 8 >= 8) {
        top = selRect.y - tbHeight - 8;
      } else {
        top = selRect.y + selRect.h - tbHeight - 8;
      }
    }

    toolbarWrap.style.left = left + 'px';
    toolbarWrap.style.top = top + 'px';
  }

  /**
   * 5. 鼠标与状态机交互
   */
  window.addEventListener('mousedown', (e) => {
    if (toolbarWrap.contains(e.target) || inlineTextInput.contains(e.target)) return;

    // 右键处理
    if (e.button === 2) {
      handleRightClick();
      return;
    }

    const x = e.clientX;
    const y = e.clientY;

    // A. 橡皮擦模式：点击删除标注
    if (isSelected && activeTool === 'eraser') {
      eraseAtPoint(x, y);
      return;
    }

    // B. 序号模式：点击打标
    if (isSelected && activeTool === 'number') {
      if (isInSelection(x, y)) {
        addNumberBadge(x, y);
      }
      return;
    }

    // C. 文字输入模式
    if (isSelected && activeTool === 'text') {
      if (isInSelection(x, y)) {
        showInlineTextInput(x, y);
      }
      return;
    }

    // D. 绘图模式 (矩形/椭圆/线条/画笔/记号笔/马赛克/模糊)
    if (isSelected && activeTool) {
      if (isInSelection(x, y)) {
        isDrawing = true;
        drawStart = { x, y };
        if (activeTool === 'brush' || activeTool === 'marker') {
          brushPoints = [{ x, y }];
        }
      }
      return;
    }

    // E. 点击 8 个拉伸调整点
    if (isSelected && e.target.classList.contains('handle')) {
      isResizing = true;
      activeHandle = e.target.dataset.handle;
      dragStart = { x, y };
      origRect = { ...selRect };
      return;
    }

    // F. 拖拽选区内部移动
    if (isSelected && isInSelection(x, y)) {
      isMoving = true;
      dragStart = { x, y };
      origRect = { ...selRect };
      return;
    }

    // G. 重新划定选区
    isSelecting = true;
    isSelected = false;
    dragStart = { x, y };
    selRect = { x, y, w: 0, h: 0 };
    clearAnnotations();
    renderBackground();
    updateSelectionUI();
  });

  window.addEventListener('mousemove', (e) => {
    const x = e.clientX;
    const y = e.clientY;

    if (!isSelected && !isSelecting) {
      updateMagnifier(x, y);
    }

    if (isSelecting) {
      const curX = Math.max(0, Math.min(screenWidth, x));
      const curY = Math.max(0, Math.min(screenHeight, y));
      selRect = {
        x: Math.min(dragStart.x, curX),
        y: Math.min(dragStart.y, curY),
        w: Math.abs(curX - dragStart.x),
        h: Math.abs(curY - dragStart.y)
      };
      renderBackground();
      updateSelectionUI();
      return;
    }

    if (isResizing && activeHandle) {
      resizeSelection(x, y);
      renderBackground();
      updateSelectionUI();
      return;
    }

    if (isMoving) {
      const dx = x - dragStart.x;
      const dy = y - dragStart.y;
      selRect.x = Math.max(0, Math.min(screenWidth - origRect.w, origRect.x + dx));
      selRect.y = Math.max(0, Math.min(screenHeight - origRect.h, origRect.y + dy));
      renderBackground();
      updateSelectionUI();
      redrawAnnotations();
      return;
    }

    if (isDrawing && activeTool) {
      renderActiveDrawing(x, y);
    }
  });

  window.addEventListener('mouseup', (e) => {
    if (isSelecting) {
      isSelecting = false;
      if (selRect.w > 8 && selRect.h > 8) {
        isSelected = true;
        magnifier.style.display = 'none';
        updateSelectionUI();
      } else {
        selRect = { x: 0, y: 0, w: 0, h: 0 };
        isSelected = false;
        renderBackground();
        updateSelectionUI();
      }
      return;
    }

    if (isResizing || isMoving) {
      isResizing = false;
      isMoving = false;
      activeHandle = null;
      updateToolbarPosition();
      return;
    }

    if (isDrawing) {
      isDrawing = false;
      commitActiveDrawing(e.clientX, e.clientY);
    }
  });

  // 滚轮快捷调节笔宽 / 颗粒度
  window.addEventListener('wheel', (e) => {
    if (!isSelected) return;
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

  function handleRightClick() {
    if (isDrawing) {
      // 取消当前临时绘制
      isDrawing = false;
      brushPoints = [];
      redrawAnnotations();
      return;
    }
    if (activeTool) {
      // 退出当前标注工具
      activeTool = null;
      toolBtns.forEach(b => b.classList.remove('active'));
      subPalette.style.display = 'none';
      updateToolbarPosition();
      return;
    }
    if (isSelected) {
      // 放弃选区重新划选
      isSelected = false;
      selRect = { x: 0, y: 0, w: 0, h: 0 };
      clearAnnotations();
      renderBackground();
      updateSelectionUI();
      return;
    }
    // 未选区退出截屏
    finishWithAction('cancel');
  }

  function isInSelection(x, y) {
    return x >= selRect.x && x <= selRect.x + selRect.w &&
           y >= selRect.y && y <= selRect.y + selRect.h;
  }

  function resizeSelection(curX, curY) {
    let { x, y, w, h } = origRect;
    const dx = curX - dragStart.x;
    const dy = curY - dragStart.y;

    if (activeHandle.includes('e')) w = Math.max(10, origRect.w + dx);
    if (activeHandle.includes('s')) h = Math.max(10, origRect.h + dy);
    if (activeHandle.includes('w')) {
      const newW = Math.max(10, origRect.w - dx);
      x = origRect.x + (origRect.w - newW);
      w = newW;
    }
    if (activeHandle.includes('n')) {
      const newH = Math.max(10, origRect.h - dy);
      y = origRect.y + (origRect.h - newH);
      h = newH;
    }

    selRect = { x, y, w, h };
  }

  /**
   * 6. 绘图与标注管线
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
    annotCtx.beginPath();
    annotCtx.rect(selRect.x, selRect.y, selRect.w, selRect.h);
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
      recordedBoxes.push({
        x: Math.round(minX - selRect.x),
        y: Math.round(minY - selRect.y),
        w: Math.round(w),
        h: Math.round(h)
      });
    } else if (activeTool === 'ellipse' && w > 4 && h > 4) {
      newEntry = { type: 'ellipse', color: activeColor, size: activeSize, isFilled, isDashed, x: minX, y: minY, w, h };
    } else if (activeTool === 'arrow' && (Math.abs(curX - drawStart.x) > 4 || Math.abs(curY - drawStart.y) > 4)) {
      newEntry = { type: 'arrow', color: activeColor, size: activeSize, isDashed, arrowStyle, x1: drawStart.x, y1: drawStart.y, x2: curX, y2: curY };
    } else if (activeTool === 'line' && (Math.abs(curX - drawStart.x) > 4 || Math.abs(curY - drawStart.y) > 4)) {
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
      redoHistory = []; // 清空重做栈
    }

    brushPoints = [];
    redrawAnnotations();
  }

  function redrawAnnotations() {
    annotCtx.clearRect(0, 0, screenWidth, screenHeight);

    annotCtx.save();
    annotCtx.beginPath();
    annotCtx.rect(selRect.x, selRect.y, selRect.w, selRect.h);
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
        annotCtx.font = `${item.italic ? 'italic ' : ''}${item.bold ? 'bold ' : ''}${item.fontSize || 16}px sans-serif`;
        if (item.hasBg) {
          const txtW = annotCtx.measureText(item.text).width;
          annotCtx.fillStyle = 'rgba(0, 0, 0, 0.65)';
          annotCtx.fillRect(item.x - 2, item.y - item.fontSize, txtW + 6, item.fontSize + 4);
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
    annotCtx.clearRect(0, 0, screenWidth, screenHeight);
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
    // 终点箭头
    ctx.beginPath();
    ctx.moveTo(x2, y2);
    ctx.lineTo(x2 - headLen * Math.cos(angle - Math.PI / 7), y2 - headLen * Math.sin(angle - Math.PI / 7));
    ctx.lineTo(x2 - (headLen * 0.7) * Math.cos(angle), y2 - (headLen * 0.7) * Math.sin(angle));
    ctx.lineTo(x2 - headLen * Math.cos(angle + Math.PI / 7), y2 - headLen * Math.sin(angle + Math.PI / 7));
    ctx.closePath();
    ctx.fill();

    // 双向箭头起点箭头
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

  function drawMosaic(ctx, mx, my, mw, mh, radius = 8) {
    const blockSize = Math.max(4, radius);
    const tempCanvas = document.createElement('canvas');
    tempCanvas.width = mw;
    tempCanvas.height = mh;
    const tCtx = tempCanvas.getContext('2d');
    tCtx.drawImage(fullSnapshotImg,
      (isRegionMode ? mx - selRect.x : mx * (fullSnapshotImg.naturalWidth / screenWidth)),
      (isRegionMode ? my - selRect.y : my * (fullSnapshotImg.naturalHeight / screenHeight)),
      mw, mh,
      0, 0, mw, mh
    );

    const imgData = tCtx.getImageData(0, 0, mw, mh);
    const data = imgData.data;

    for (let y = 0; y < mh; y += blockSize) {
      for (let x = 0; x < mw; x += blockSize) {
        const pIndex = (y * mw + x) * 4;
        ctx.fillStyle = `rgb(${data[pIndex]},${data[pIndex + 1]},${data[pIndex + 2]})`;
        ctx.fillRect(mx + x, my + y, blockSize, blockSize);
      }
    }
  }

  function drawBlur(ctx, mx, my, mw, mh, radius = 8) {
    ctx.save();
    ctx.filter = `blur(${Math.max(2, Math.round(radius / 2))}px)`;
    ctx.drawImage(fullSnapshotImg,
      (isRegionMode ? mx - selRect.x : mx * (fullSnapshotImg.naturalWidth / screenWidth)),
      (isRegionMode ? my - selRect.y : my * (fullSnapshotImg.naturalHeight / screenHeight)),
      mw, mh,
      mx, my, mw, mh
    );
    ctx.restore();
  }

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

  function eraseAtPoint(x, y) {
    // 倒序寻找命中的标注
    for (let i = annotHistory.length - 1; i >= 0; i--) {
      const it = annotHistory[i];
      let hit = false;
      if (it.type === 'rect' || it.type === 'ellipse' || it.type === 'mosaic' || it.type === 'blur') {
        hit = x >= it.x - 4 && x <= it.x + it.w + 4 && y >= it.y - 4 && y <= it.y + it.h + 4;
      } else if (it.type === 'number') {
        hit = Math.hypot(x - it.x, y - it.y) <= 15;
      } else if (it.type === 'text') {
        hit = x >= it.x - 4 && x <= it.x + 100 && y >= it.y - 20 && y <= it.y + 4;
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

  function showInlineTextInput(x, y) {
    inlineTextInput.style.display = 'block';
    inlineTextInput.style.left = x + 'px';
    inlineTextInput.style.top = y + 'px';
    inlineTextInput.style.color = activeColor;
    inlineTextInput.style.fontSize = activeFontSize + 'px';
    inlineTextInput.style.fontWeight = isFontBold ? 'bold' : 'normal';
    inlineTextInput.style.fontStyle = isFontItalic ? 'italic' : 'normal';
    inlineTextInput.value = '';
    inlineTextInput.focus();

    const commitText = () => {
      const val = inlineTextInput.value.trim();
      if (val) {
        annotHistory.push({
          type: 'text',
          text: val,
          x: x,
          y: y + activeFontSize,
          color: activeColor,
          fontSize: activeFontSize,
          bold: isFontBold,
          italic: isFontItalic,
          hasBg: hasTextBg
        });
        recordedTextNotes.push(val); // 记录文本指令供 AI 改图继承
        redoHistory = [];
        redrawAnnotations();
      }
      inlineTextInput.style.display = 'none';
      inlineTextInput.removeEventListener('blur', commitText);
    };

    inlineTextInput.addEventListener('blur', commitText);
    inlineTextInput.addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter' && !ev.shiftKey) {
        ev.preventDefault();
        inlineTextInput.blur();
      }
    });
  }

  /**
   * 7. 工具栏与二级属性交互
   */
  toolBtns.forEach(btn => {
    btn.addEventListener('click', () => {
      const tool = btn.dataset.tool;
      if (activeTool === tool) {
        // 复合按钮再次点击切换子工具
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
        } else {
          // 非复合按钮关闭
          activeTool = null;
          btn.classList.remove('active');
          subPalette.style.display = 'none';
          updateToolbarPosition();
          return;
        }
      } else {
        toolBtns.forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        activeTool = tool;
      }

      openSubPaletteForTool(activeTool);
      updateToolbarPosition();
    });
  });

  function openSubPaletteForTool(tool) {
    if (!tool) {
      subPalette.style.display = 'none';
      return;
    }
    subPalette.style.display = 'flex';

    // 隐藏所有特定子区
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
    } else if (tool === 'mosaic' || tool === 'blur') {
      subMosaicOptions.style.display = 'flex';
      blurModeLabel.textContent = tool === 'blur' ? '平滑模糊' : '马赛克';
      subParamBadge.textContent = mosaicRadius + 'px';
    } else if (tool === 'number') {
      subNumberOptions.style.display = 'flex';
      subParamBadge.textContent = '序号';
    } else if (tool === 'text') {
      subTextOptions.style.display = 'flex';
      subParamBadge.textContent = activeFontSize + 'px';
    } else if (tool === 'marker') {
      subParamBadge.textContent = (activeSize * 3) + 'px';
    } else {
      subParamBadge.textContent = activeSize + 'px';
    }
  }

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
    });
  });

  // 原生拾色器
  currentSwatch.addEventListener('click', () => nativeColorPicker.click());
  nativeColorPicker.addEventListener('input', () => {
    activeColor = nativeColorPicker.value;
    currentSwatch.style.backgroundColor = activeColor;
    palDots.forEach(d => d.classList.remove('active'));
  });

  // 撤销 (Ctrl+Z)
  btnUndo.addEventListener('click', () => {
    if (annotHistory.length > 0) {
      const popped = annotHistory.pop();
      redoHistory.push(popped);
      if (popped.type === 'rect') recordedBoxes.pop();
      if (popped.type === 'text') recordedTextNotes.pop();
      redrawAnnotations();
    }
  });

  // 重做 (Ctrl+Y)
  btnRedo.addEventListener('click', () => {
    if (redoHistory.length > 0) {
      const popped = redoHistory.pop();
      annotHistory.push(popped);
      if (popped.type === 'rect') {
        recordedBoxes.push({
          x: Math.round(popped.x - selRect.x),
          y: Math.round(popped.y - selRect.y),
          w: Math.round(popped.w),
          h: Math.round(popped.h)
        });
      }
      if (popped.type === 'text') recordedTextNotes.push(popped.text);
      redrawAnnotations();
    }
  });

  /**
   * 8. 三图分离导出与动作派发
   */
  function exportCaptureData() {
    if (selRect.w <= 0 || selRect.h <= 0) return null;

    let realW = selRect.w;
    let realH = selRect.h;
    const cleanCanvas = document.createElement('canvas');
    const cCtx = cleanCanvas.getContext('2d');

    if (isRegionMode) {
      realW = fullSnapshotImg.naturalWidth;
      realH = fullSnapshotImg.naturalHeight;
      cleanCanvas.width = realW;
      cleanCanvas.height = realH;
      cCtx.drawImage(fullSnapshotImg, 0, 0, realW, realH);
    } else {
      const scaleX = fullSnapshotImg.naturalWidth / screenWidth;
      const scaleY = fullSnapshotImg.naturalHeight / screenHeight;
      const realX = Math.round(selRect.x * scaleX);
      const realY = Math.round(selRect.y * scaleY);
      realW = Math.round(selRect.w * scaleX);
      realH = Math.round(selRect.h * scaleY);
      cleanCanvas.width = realW;
      cleanCanvas.height = realH;
      cCtx.drawImage(fullSnapshotImg, realX, realY, realW, realH, 0, 0, realW, realH);
    }
    const cleanDataUrl = cleanCanvas.toDataURL('image/png');

    // 2. 带涂鸦的完整图
    const annotCanvasMerged = document.createElement('canvas');
    annotCanvasMerged.width = cleanCanvas.width;
    annotCanvasMerged.height = cleanCanvas.height;
    const aCtx = annotCanvasMerged.getContext('2d');
    aCtx.drawImage(cleanCanvas, 0, 0);
    aCtx.drawImage(annotCanvas,
      selRect.x * dpr, selRect.y * dpr, selRect.w * dpr, selRect.h * dpr,
      0, 0, cleanCanvas.width, cleanCanvas.height
    );
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

    try {
      localStorage.setItem('rubick_last_capture_result', JSON.stringify({
        action: actionType,
        data: data,
        timestamp: Date.now()
      }));
    } catch (e) {}

    if (window.onCaptureFinished) {
      window.onCaptureFinished(actionType, data);
    } else if (window.opener && window.opener.onSubWindowCaptureFinished) {
      window.opener.onSubWindowCaptureFinished(actionType, data);
      window.close();
    } else {
      window.close();
    }
  }

  // 动作按钮绑定
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

  // 双击选区直接复制完成
  selectionBox.addEventListener('dblclick', () => finishWithAction('copy'));

  // 键盘全局快捷键 (1:1 对齐 Snipaste 快捷键表)
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
    } else if (e.key === 'c' || e.key === 'C') {
      const hex = magnifier.dataset.currentColor;
      if (hex && !isSelected) {
        navigator.clipboard.writeText(hex);
        magHexText.textContent = '已复制 ' + hex;
      }
    } else if (!e.ctrlKey && !e.metaKey && !e.altKey) {
      // 字母快捷键直切工具
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
