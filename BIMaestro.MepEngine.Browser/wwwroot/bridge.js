const ready = Blazor.start();
window.addEventListener('message', async (event) => {
  if (event.origin !== location.origin || event.source !== parent || event.data?.type !== 'mep-calculate') return;
  const { id, json } = event.data;
  try {
    await ready;
    const result = await DotNet.invokeMethodAsync('BIMaestro.MepEngine.Browser', 'Calculate', json);
    parent.postMessage({ type: 'mep-result', id, result }, location.origin);
  } catch (error) {
    parent.postMessage({ type: 'mep-result', id, error: String(error) }, location.origin);
  }
});
ready.then(() => parent.postMessage({ type: 'mep-ready' }, location.origin)).catch(error => parent.postMessage({ type: 'mep-error', error: String(error) }, location.origin));
