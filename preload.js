// Что страница может попросить у окна (из меню и при перетаскивании) и что окно сообщает странице.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('dice', {
  setOnTop: v => ipcRenderer.send('on-top', v),
  close: () => ipcRenderer.send('close'),
  moveTo: (x, y, r) => ipcRenderer.send('move-to', x, y, r),
  ignoreMouse: v => ipcRenderer.send('ignore-mouse', v),
  onArea: cb => ipcRenderer.on('area', (e, a) => cb(a)),   // видимая часть экрана в координатах окна
  onRoll: cb => ipcRenderer.on('roll', () => cb()),        // горячая клавиша PgUp
});
