import { SbGate } from './sb-gate.js';
import { SbCase, SbAdapt } from './sb-adapt.js';
import { SbWidget } from './sb-widget.js';
import { sbCoordinator } from './coordinator.js';
export { SbGate, SbCase, SbAdapt, SbWidget, sbCoordinator };
if (typeof customElements !== 'undefined') {
    customElements.define('sb-gate', SbGate);
    customElements.define('sb-case', SbCase);
    customElements.define('sb-adapt', SbAdapt);
    customElements.define('sb-widget', SbWidget);
}
