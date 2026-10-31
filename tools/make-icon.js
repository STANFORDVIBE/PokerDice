// Рисует иконку тем же 3D-кубиком, что и в приложении, и собирает icon.ico (16–256 px).
// Запуск: npm run icon
const { app, BrowserWindow, nativeImage } = require('electron');
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256];

// ICO с PNG внутри: заголовок, каталог картинок, сами PNG
function packIco(pngs) {
  const head = Buffer.alloc(6 + 16 * pngs.length);
  head.writeUInt16LE(1, 2);
  head.writeUInt16LE(pngs.length, 4);
  let offset = head.length;
  pngs.forEach(({ size, data }, i) => {
    const e = 6 + 16 * i;
    head.writeUInt8(size >= 256 ? 0 : size, e);
    head.writeUInt8(size >= 256 ? 0 : size, e + 1);
    head.writeUInt16LE(1, e + 4);
    head.writeUInt16LE(32, e + 6);
    head.writeUInt32LE(data.length, e + 8);
    head.writeUInt32LE(offset, e + 12);
    offset += data.length;
  });
  return Buffer.concat([head, ...pngs.map(p => p.data)]);
}

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 600, height: 600, show: false, frame: false, transparent: true });
  await win.loadFile(path.join(ROOT, 'ui', 'index.html'), { query: { icon: '1' } });
  const big = nativeImage.createFromDataURL(await win.webContents.executeJavaScript('makeIcon(1024)'));
  const pngs = SIZES.map(size => ({ size, data: big.resize({ width: size, height: size, quality: 'best' }).toPNG() }));
  fs.writeFileSync(path.join(ROOT, 'icon.ico'), packIco(pngs));
  fs.writeFileSync(path.join(ROOT, 'icon.png'), pngs.at(-1).data);
  app.quit();
});
