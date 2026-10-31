// Что страница может попросить у окна (из меню и при перетаскивании).
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('dice', {
  setOnTop: v => ipcRenderer.send('on-top', v),
  close: () => ipcRenderer.send('close'),
  moveTo: (x, y) => ipcRenderer.send('move-to', x, y),
  ignoreMouse: v => ipcRenderer.send('ignore-mouse', v),
});
