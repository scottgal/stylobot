(() => {
  let connection;
  const listeners = new Set();

  async function ensureConnection(hubPath) {
    if (connection) return connection;
    if (!window.signalR) throw new Error("Signal Shingle requires the SignalR browser client.");
    connection = new signalR.HubConnectionBuilder().withUrl(hubPath).withAutomaticReconnect().build();
    connection.on('Dirty', (key) => listeners.forEach((listener) => listener(key)));
    await connection.start();
    return connection;
  }

  window.signalShingle = () => ({
    async connect(element) {
      const key = element.dataset.signalShingleKey;
      const hub = await ensureConnection(element.dataset.signalShingleHub || '/_signal-shingle-hub');
      const endpoint = element.dataset.signalShingleEndpoint || '/_signal-shingle';
      await hub.invoke('Join', key);
      listeners.add(async (changedKey) => {
        if (changedKey !== key) return;
        const response = await fetch(`${endpoint}/${encodeURIComponent(key)}`);
        if (response.ok) element.innerHTML = await response.text();
      });
    }
  });
})();
