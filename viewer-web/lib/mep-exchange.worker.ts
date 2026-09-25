import { annotationExchange, annotationsIfc, reservationExchange, reservationsIfc } from './mep-exchange';

self.onmessage = (event: MessageEvent<{ args: Parameters<typeof reservationExchange>; ifc: boolean; annotations?: boolean }>) => {
  try {
    if (event.data.annotations) {
      self.postMessage({ content: annotationsIfc(annotationExchange(event.data.args[0], event.data.args[1], event.data.args[2])) });
      return;
    }
    const exchange = reservationExchange(...event.data.args);
    if (!exchange.reservations.length) throw new Error('Aucune réservation confirmée à exporter.');
    self.postMessage({ content: event.data.ifc ? reservationsIfc(exchange) : JSON.stringify(exchange, null, 2) });
  } catch (error) {
    self.postMessage({ error: error instanceof Error ? error.message : 'Export impossible' });
  }
};
