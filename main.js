// Кубик: маленькое прозрачное окно без рамки — на экране только кубик.
// Весь интерфейс и анимация — в ui/index.html.
const { app, BrowserWindow, ipcMain, screen, globalShortcut } = require('electron');
const fs = require('fs');
const path = require('path');

const DEMO = process.argv.includes('--demo'); // для проверки: бросить, увеличить, открыть меню, закрыться
const WIDTH = 575, HEIGHT = 625;              // окно постоянного размера, кубик в его центре (как в ui/index.html)
const posFile = () => path.join(app.getPath('userData'), 'position.json');

// где стоял кубик в прошлый раз (если тот экран всё ещё подключён); храним центр кубика
function loadPos() {
  try {
    const saved = JSON.parse(fs.readFileSync(posFile(), 'utf8'));
    // старые версии хранили левый верхний угол окна 230×250, центр кубика был в (115, 104)
    const cx = saved.cx ?? saved.x + 115, cy = saved.cy ?? saved.y + 104;
    const area = screen.getDisplayNearestPoint({ x: cx, y: cy }).workArea;
    if (cx >= area.x && cy >= area.y && cx <= area.x + area.width && cy <= area.y + area.height)
      return { x: Math.round(cx - WIDTH / 2), y: Math.round(cy - HEIGHT / 2) };
  } catch (e) {}
  return {};
}

function savePos(win) {
  try {
    const [x, y] = win.getPosition();
    fs.mkdirSync(path.dirname(posFile()), { recursive: true });
    fs.writeFileSync(posFile(), JSON.stringify({ cx: x + WIDTH / 2, cy: y + HEIGHT / 2 }));
  } catch (e) {}
}

// рабочая область экрана, на котором стоит кубик, — в координатах окна: по ней меню и подсказка не уезжают за край
function areaOf(win) {
  const [x, y] = win.getPosition();
  const a = screen.getDisplayNearestPoint({ x: x + WIDTH / 2, y: y + HEIGHT / 2 }).workArea;
  return { left: a.x - x, top: a.y - y, right: a.x + a.width - x, bottom: a.y + a.height - y };
}

if (!app.requestSingleInstanceLock()) app.quit();

app.whenReady().then(() => {
  const win = new BrowserWindow({
    width: WIDTH, height: HEIGHT, ...(DEMO ? { x: 1250, y: 250 } : loadPos()),
    title: 'Кубик', icon: path.join(__dirname, 'icon.ico'),
    transparent: true, backgroundColor: '#00000000', frame: false, hasShadow: false,
    resizable: false, maximizable: false, fullscreenable: false,
    alwaysOnTop: DEMO,
    webPreferences: { preload: path.join(__dirname, 'preload.js') },
  });
  win.setMenu(null);
  win.loadFile(path.join(__dirname, 'ui', 'index.html'));

  // размер держим сами: на экранах с масштабом прозрачное окно иначе «растёт» при перетаскивании
  const size = { width: WIDTH, height: HEIGHT };
  ipcMain.on('on-top', (e, v) => win.setAlwaysOnTop(!!v));
  ipcMain.on('close', () => win.close());
  const sendArea = () => win.webContents.send('area', areaOf(win));
  // кубик (r — его радиус в пикселях) целиком остаётся на экране, куда его тащат
  ipcMain.on('move-to', (e, x, y, r = 0) => {
    const cx = x + WIDTH / 2, cy = y + HEIGHT / 2;
    const a = screen.getDisplayNearestPoint({ x: Math.round(cx), y: Math.round(cy) }).workArea;
    const fit = (v, lo, hi) => Math.max(lo, Math.min(v, hi));
    x = fit(cx, a.x + r, a.x + a.width - r) - WIDTH / 2;
    y = fit(cy, a.y + r, a.y + a.height - r) - HEIGHT / 2;
    win.setBounds({ x: Math.round(x), y: Math.round(y), ...size });
    sendArea();
  });
  win.webContents.on('did-finish-load', sendArea);
  screen.on('display-metrics-changed', sendArea);
  screen.on('display-removed', sendArea);

  // PgUp бросает кубик из любого окна: кубик всплывает поверх остальных, но фокус у игры не забирает
  globalShortcut.register('PageUp', () => { win.moveTop(); win.webContents.send('roll'); });
  // пустые прозрачные места пропускают клики к тому, что под окном
  ipcMain.on('ignore-mouse', (e, v) => win.setIgnoreMouseEvents(!!v, { forward: true }));

  app.on('second-instance', () => { win.show(); win.focus(); });
  if (!DEMO) win.on('close', () => savePos(win));

  if (DEMO) win.webContents.once('did-finish-load', () => {
    const js = code => win.webContents.executeJavaScript(code);
    setTimeout(() => js('roll()'), 1200);
    setTimeout(() => js('setScale(1.6); roll()'), 3800);
    setTimeout(() => js('openMenu(150, 60)'), 6500);
    setTimeout(() => js('setScale(1)').then(() => app.quit()), 9000);
  });
});

app.on('will-quit', () => globalShortcut.unregisterAll());
app.on('window-all-closed', () => app.quit());
